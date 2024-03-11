﻿using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Template;
using Smartstore.Core.Checkout.Cart;
using Smartstore.Core.Checkout.Cart.Events;
using Smartstore.Core.Checkout.Payment;
using Smartstore.Core.Data;
using Smartstore.Core.Localization;
using Smartstore.Core.Logging;
using Smartstore.Core.Stores;
using Smartstore.Core.Web;
using Smartstore.Events;
using Smartstore.Utilities.Html;

namespace Smartstore.Core.Checkout.Orders
{
    public partial class CheckoutWorkflow : ICheckoutWorkflow
    {
        const int _maxWarnings = 3;

        private readonly SmartDbContext _db;
        private readonly IStoreContext _storeContext;
        private readonly INotifier _notifier;
        private readonly ILogger _logger;
        private readonly IWebHelper _webHelper;
        private readonly IEventPublisher _eventPublisher;
        private readonly IShoppingCartValidator _shoppingCartValidator;
        private readonly IOrderProcessingService _orderProcessingService;
        private readonly IPaymentService _paymentService;
        private readonly ICheckoutHandler[] _handlers;
        private readonly ICheckoutStateAccessor _checkoutStateAccessor;
        private readonly OrderSettings _orderSettings;
        private readonly ShoppingCartSettings _shoppingCartSettings;

        public CheckoutWorkflow(
            SmartDbContext db,
            IStoreContext storeContext,
            INotifier notifier,
            ILogger logger,
            IWebHelper webHelper,
            IEventPublisher eventPublisher,
            IShoppingCartValidator shoppingCartValidator,
            IOrderProcessingService orderProcessingService,
            IPaymentService paymentService,
            IEnumerable<ICheckoutHandler> handlers,
            ICheckoutStateAccessor checkoutStateAccessor,
            OrderSettings orderSettings,
            ShoppingCartSettings shoppingCartSettings)
        {
            _db = db;
            _storeContext = storeContext;
            _notifier = notifier;
            _logger = logger;
            _webHelper = webHelper;
            _eventPublisher = eventPublisher;
            _shoppingCartValidator = shoppingCartValidator;
            _orderProcessingService = orderProcessingService;
            _paymentService = paymentService;
            _checkoutStateAccessor = checkoutStateAccessor;
            _orderSettings = orderSettings;
            _shoppingCartSettings = shoppingCartSettings;

            _handlers = handlers.OrderBy(x => x.Order).ToArray();
        }

        public Localizer T { get; set; } = NullLocalizer.Instance;

        public virtual async Task<CheckoutWorkflowResult> StartAsync(CheckoutContext context)
        {
            Guard.NotNull(context);

            var warnings = new List<string>();
            var cart = context.Cart;

            var preliminaryResult = Preliminary(context);
            if (preliminaryResult != null)
            {
                return new(preliminaryResult);
            }

            cart.Customer.ResetCheckoutData(cart.StoreId);
            _checkoutStateAccessor.Abandon();

            if (await _shoppingCartValidator.ValidateCartAsync(cart, warnings, true))
            {
                var validatingCartEvent = new ValidatingCartEvent(cart, warnings);
                await _eventPublisher.PublishAsync(validatingCartEvent);

                if (validatingCartEvent.Result != null)
                {
                    return new(validatingCartEvent.Result);
                }

                // Validate each shopping cart item.
                foreach (var item in cart.Items)
                {
                    if (warnings.Count > 0)
                    {
                        break;
                    }

                    var addToCartContext = new AddToCartContext
                    {
                        StoreId = cart.StoreId,
                        Product = item.Item.Product,
                        BundleItem = item.Item.BundleItem,
                        ChildItems = item.ChildItems.Select(x => x.Item).ToList()
                    };

                    if (!await _shoppingCartValidator.ValidateAddToCartItemAsync(addToCartContext, item.Item, cart.Items))
                    {
                        warnings.AddRange(addToCartContext.Warnings);
                    }
                }
            }

            await _db.SaveChangesAsync();

            if (warnings.Count > 0)
            {
                warnings.Take(_maxWarnings).Each(x => _notifier.Warning(x));
                return new(RedirectToCart());
            }

            return await AdvanceAsync(context);
        }

        public virtual async Task<CheckoutWorkflowResult> ProcessAsync(CheckoutContext context)
        {
            Guard.NotNull(context);

            var preliminaryResult = Preliminary(context);
            if (preliminaryResult != null)
            {
                return new(preliminaryResult);
            }

            // Get and process the current handler, based on the request's route values.
            var handler = _handlers.FirstOrDefault(x => x.IsHandlerFor(context));
            if (handler != null)
            {
                var result = await handler.ProcessAsync(context);
                if (result.SkipPage)
                {
                    // Current checkout page should be skipped. For example there is only one shipping method
                    // and the customer has nothing to select on the associated page.
                    return new(result.ActionResult ?? Adjacent(handler, context));
                }

                // No redirect (default). Opening the current checkout page is fine.
                return new(null, result.Errors);
            }

            return new(null);
        }

        public virtual async Task<CheckoutWorkflowResult> AdvanceAsync(CheckoutContext context)
        {
            Guard.NotNull(context);

            var preliminaryResult = Preliminary(context);
            if (preliminaryResult != null)
            {
                return new(preliminaryResult);
            }

            if (_shoppingCartSettings.QuickCheckoutEnabled)
            {
                // Process all handlers in sequence. Open the checkout page associated with the first handler that reports "unsuccessful".
                foreach (var handler in _handlers)
                {
                    var result = await handler.ProcessAsync(context);
                    if (!result.Success)
                    {
                        // Redirect to the checkout page associated with the "unsuccessful" handler.
                        return new(result.ActionResult ?? handler.GetActionResult(context), result.Errors);
                    }
                }

                // Processing of all handlers was successful -> redirect to confirm.
                return new(RedirectToCheckout("Confirm"));
            }
            else
            {
                if (context.IsCurrentRoute(null, "Index"))
                {
                    return new(_handlers[0].GetActionResult(context));
                }

                // Get current handler, based on the request's route values.
                var handler = _handlers.FirstOrDefault(x => x.IsHandlerFor(context));
                if (handler != null)
                {
                    var result = await handler.ProcessAsync(context);
                    if (!result.Success)
                    {
                        // Redirect to the checkout page associated with the "unsuccessful" handler.
                        return new(result.ActionResult ?? handler.GetActionResult(context), result.Errors);
                    }

                    // Current handler is the last one -> redirect ro confirm.
                    if (handler.Equals(_handlers[^1]))
                    {
                        return new(RedirectToCheckout("Confirm"));
                    }

                    // Redirect to the checkout page associated with the next handler.
                    var nextHandler = GetNextHandler(handler, true);
                    if (nextHandler != null)
                    {
                        return new(nextHandler.GetActionResult(context));
                    }
                }

                // A redirect target cannot be determined.
                return new(null);
            }
        }

        public virtual async Task<CheckoutWorkflowResult> CompleteAsync(CheckoutContext context)
        {
            Guard.NotNull(context);

            var warnings = new List<string>();
            var store = _storeContext.CurrentStore;
            var cart = context.Cart;
            OrderPlacementResult placeOrderResult = null;

            var validatingCartEvent = new ValidatingCartEvent(cart, warnings);
            await _eventPublisher.PublishAsync(validatingCartEvent);

            if (validatingCartEvent.Result != null)
            {
                return new(validatingCartEvent.Result);
            }

            if (warnings.Count > 0)
            {
                warnings.Take(_maxWarnings).Each(x => _notifier.Warning(x));
                return new(RedirectToCart());
            }

            // Prevent two orders from being placed within a time span of x seconds.
            if (!await _orderProcessingService.IsMinimumOrderPlacementIntervalValidAsync(cart.Customer, store))
            {
                _notifier.Warning(T("Checkout.MinOrderPlacementInterval"));
                return new(RedirectToCheckout("Confirm"));
            }

            try
            {
                context.HttpContext.Session.TryGetObject<ProcessPaymentRequest>(CheckoutState.OrderPaymentInfoName, out var paymentRequest);
                paymentRequest ??= new();
                paymentRequest.StoreId = store.Id;
                paymentRequest.CustomerId = cart.Customer.Id;
                paymentRequest.PaymentMethodSystemName = cart.Customer.GenericAttributes.SelectedPaymentMethod;

                var placeOrderExtraData = new Dictionary<string, string>
                {
                    ["CustomerComment"] = context.HttpContext.Request.Form["customercommenthidden"].ToString(),
                    ["SubscribeToNewsletter"] = context.HttpContext.Request.Form["SubscribeToNewsletter"].ToString(),
                    ["AcceptThirdPartyEmailHandOver"] = context.HttpContext.Request.Form["AcceptThirdPartyEmailHandOver"].ToString()
                };

                placeOrderResult = await _orderProcessingService.PlaceOrderAsync(paymentRequest, placeOrderExtraData);
            }
            catch (PaymentException ex)
            {
                return new(PaymentFailure(ex));
            }
            catch (Exception ex)
            {
                _logger.Error(ex);

                return new(null, [new(string.Empty, ex.Message)]);
            }

            if (placeOrderResult == null || !placeOrderResult.Success)
            {
                var errors = placeOrderResult?.Errors
                    ?.Take(_maxWarnings)
                    ?.Select(x => new CheckoutWorkflowError(string.Empty, HtmlUtility.ConvertPlainTextToHtml(x)))
                    ?.ToArray();

                return new(null, errors);
            }

            var postPaymentRequest = new PostProcessPaymentRequest
            {
                Order = placeOrderResult.PlacedOrder
            };

            try
            {
                await _paymentService.PostProcessPaymentAsync(postPaymentRequest);
            }
            catch (PaymentException ex)
            {
                return new(PaymentFailure(ex));
            }
            catch (Exception ex)
            {
                _notifier.Error(ex.Message);
            }
            finally
            {
                context.HttpContext.Session.TrySetObject<ProcessPaymentRequest>(CheckoutState.OrderPaymentInfoName, null);
                _checkoutStateAccessor.Abandon();
            }

            if (postPaymentRequest.RedirectUrl.HasValue())
            {
                return new(new RedirectResult(postPaymentRequest.RedirectUrl));
            }

            return new(RedirectToCheckout("Completed"));

            RedirectToActionResult PaymentFailure(PaymentException ex)
            {
                _logger.Error(ex);
                _notifier.Error(ex.Message);

                if (ex.RedirectRoute != null)
                {
                    return new RedirectToActionResult(ex.RedirectRoute.Action, ex.RedirectRoute.Controller, ex.RedirectRoute.RouteValues);
                }

                return RedirectToCheckout("PaymentMethod");
            }
        }

        /// <summary>
        /// Checks whether the checkout can be executed, e.g. whether the shopping cart has items.
        /// </summary>
        private IActionResult Preliminary(CheckoutContext context)
        {
            if (context.HttpContext?.Request == null)
            {
                throw new InvalidOperationException("The checkout workflow is only applicable in the context of a HTTP request.");
            }

            if (_handlers.Length == 0)
            {
                throw new InvalidOperationException("No checkout handlers found.");
            }

            if (!_orderSettings.AnonymousCheckoutAllowed && !context.Cart.Customer.IsRegistered())
            {
                return new ChallengeResult();
            }

            if (!context.Cart.HasItems)
            {
                return RedirectToCart();
            }

            return null;
        }

        /// <summary>
        /// Special case when the checkout page associated with <paramref name="handler"/> must always be skipped
        /// (e.g. if the store only offers a single shipping method).
        /// In this case, based on the referrer, the customer must be redirected to the next or previous page,
        /// depending on the direction from which the customer accessed the current page.
        /// </summary>
        private IActionResult Adjacent(ICheckoutHandler handler, CheckoutContext context)
        {
            // Get route values of the URL referrer.
            var referrer = _webHelper.GetUrlReferrer();
            var path = referrer?.PathAndQuery;
            var routeValues = new RouteValueDictionary();

            if (path.HasValue())
            {
                var values = new RouteValueDictionary();
                var template = TemplateParser.Parse("{controller}/{action}/{id?}");
                var matcher = new TemplateMatcher(template, []);
                matcher.TryMatch(path, routeValues);
            }

            var next = true;
            var action = routeValues.GetActionName();
            var controller = routeValues.GetControllerName();

            if (action.HasValue() && controller.HasValue())
            {
                if (action.EqualsNoCase("Index") && controller.EqualsNoCase("Checkout"))
                {
                    // Referrer is the checkout index page -> return the next handler (billing address).
                    next = true;
                }
                else if (action.EqualsNoCase("Confirm") && controller.EqualsNoCase("Checkout"))
                {
                    // Referrer is the confirm page -> return the previous handler (payment selection).
                    next = false;
                }
                else
                {
                    // Referrer is any step in checkout -> return the next handler if the referrer's order number
                    // is less than that of the current handler. Otherwise return previous handler.
                    var referrerHandler = _handlers.FirstOrDefault(x => x.IsHandlerFor(context));
                    next = (referrerHandler?.Order ?? 0) < handler.Order;
                }
            }

            var result = GetNextHandler(handler, next)?.GetActionResult(context);
            result ??= next ? RedirectToCheckout("Confirm") : RedirectToCart();

            return result;
        }

        /// <summary>
        /// Gets the next/previous checkout handler depending on <paramref name="handler"/>.
        /// </summary>
        /// <param name="handler">Current handler to get the next/previous checkout handler for.</param>
        /// <param name="next"><c>true</c> to get the next, <c>false</c> to get the previous handler.</param>
        private ICheckoutHandler GetNextHandler(ICheckoutHandler handler, bool next)
        {
            if (next)
            {
                return _handlers
                    .Where(x => x.Order > handler.Order)
                    .OrderBy(x => x.Order)
                    .FirstOrDefault();
            }
            else
            {
                return _handlers
                    .Where(x => x.Order < handler.Order)
                    .OrderByDescending(x => x.Order)
                    .FirstOrDefault();
            }
        }

        private static RedirectToActionResult RedirectToCheckout(string action)
            => new(action, "Checkout", null);

        // INFO: do not use RedirectToRouteResult here. It would create an infinite redirection loop.
        // In CheckoutWorkflow always use RedirectToActionResult with controller and action name.
        private static RedirectToActionResult RedirectToCart()
            => new("Cart", "ShoppingCart", null);
    }
}