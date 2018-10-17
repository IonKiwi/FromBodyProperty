using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Internal;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace WebApplication.Logic {
	/// <summary>
	/// An <see cref="IModelBinder"/> which binds models from the request body json properties using an <see cref="IInputFormatter"/>.
	/// </summary>
	public class FromBodyPropertyModelBinder : IModelBinder {
		private readonly IList<IInputFormatter> _formatters;
		private readonly Func<Stream, Encoding, TextReader> _readerFactory;
		private readonly ILogger _logger;
		private readonly MvcOptions _options;

		private static readonly Action<ILogger, IInputFormatter, string, Exception> _inputFormatterSelected;
		private static readonly Action<ILogger, IInputFormatter, string, Exception> _inputFormatterRejected;
		private static readonly Action<ILogger, string, Exception> _noInputFormatterSelected;
		private static readonly Action<ILogger, string, string, Exception> _removeFromBodyAttribute;

		static FromBodyPropertyModelBinder() {
			_inputFormatterSelected = LoggerMessage.Define<IInputFormatter, string>(
								LogLevel.Debug,
								1,
								"Selected input formatter '{InputFormatter}' for content type '{ContentType}'.");

			_inputFormatterRejected = LoggerMessage.Define<IInputFormatter, string>(
					LogLevel.Debug,
					2,
					"Rejected input formatter '{InputFormatter}' for content type '{ContentType}'.");

			_noInputFormatterSelected = LoggerMessage.Define<string>(
								LogLevel.Debug,
								3,
								"No input formatter was found to support the content type '{ContentType}' for use with the [FromBody] attribute.");

			_removeFromBodyAttribute = LoggerMessage.Define<string, string>(
								LogLevel.Debug,
								4,
								"To use model binding, remove the [FromBody] attribute from the property or parameter named '{ModelName}' with model type '{ModelType}'.");
		}

		/// <summary>
		/// Creates a new <see cref="FromBodyPropertyModelBinder"/>.
		/// </summary>
		/// <param name="formatters">The list of <see cref="IInputFormatter"/>.</param>
		/// <param name="readerFactory">
		/// The <see cref="IHttpRequestStreamReaderFactory"/>, used to create <see cref="System.IO.TextReader"/>
		/// instances for reading the request body.
		/// </param>
		/// <param name="loggerFactory">The <see cref="ILoggerFactory"/>.</param>
		public FromBodyPropertyModelBinder(
				IOptions<MvcOptions> mvcOptions,
				IHttpRequestStreamReaderFactory readerFactory,
				ILoggerFactory loggerFactory) {

			if (readerFactory == null) {
				throw new ArgumentNullException(nameof(readerFactory));
			}

			_formatters = mvcOptions.Value.InputFormatters;
			_readerFactory = readerFactory.CreateReader;

			if (loggerFactory != null) {
				_logger = loggerFactory.CreateLogger<FromBodyPropertyModelBinder>();
			}

			_options = mvcOptions.Value;
		}

		/// <inheritdoc />
		public async Task BindModelAsync(ModelBindingContext bindingContext) {
			if (bindingContext == null) {
				throw new ArgumentNullException(nameof(bindingContext));
			}

			// Special logic for body, treat the model name as string.Empty for the top level
			// object, but allow an override via BinderModelName. The purpose of this is to try
			// and be similar to the behavior for POCOs bound via traditional model binding.
			string modelBindingKey;
			if (bindingContext.IsTopLevelObject) {
				modelBindingKey = bindingContext.BinderModelName ?? string.Empty;
			}
			else {
				modelBindingKey = bindingContext.ModelName;
			}

			var httpContext = bindingContext.HttpContext;

			var allowEmptyInputInModelBinding = _options?.AllowEmptyInputInBodyModelBinding == true;

			var serviceProvider = httpContext.RequestServices;
			var bodyModelBinderHelper = serviceProvider.GetService<IFromBodyPropertyModelBinderHelper>();
			bodyModelBinderHelper.Initialize(bindingContext.ActionContext.ActionDescriptor.Id, bindingContext.ActionContext.ActionDescriptor.Parameters.Concat(bindingContext.ActionContext.ActionDescriptor.BoundProperties));

			var formatterContext = new FromBodyPropertyInputFormatterContext(
					httpContext,
					modelBindingKey,
					bindingContext.ModelState,
					bindingContext.ModelMetadata,
					_readerFactory,
					allowEmptyInputInModelBinding,
					bindingContext.ActionContext.ActionDescriptor.Id,
					bindingContext.FieldName,
					bodyModelBinderHelper);

			var formatter = (IInputFormatter)null;
			for (var i = 0; i < _formatters.Count; i++) {
				if (_formatters[i].CanRead(formatterContext)) {
					formatter = _formatters[i];
					InputFormatterSelected(_logger, formatter, formatterContext);
					break;
				}
				else {
					InputFormatterRejected(_logger, _formatters[i], formatterContext);
				}
			}

			if (formatter == null) {
				NoInputFormatterSelected(_logger, formatterContext);
				var message = string.Format("UnsupportedContentType: {0}", httpContext.Request.ContentType);
				var exception = new UnsupportedContentTypeException(message);
				bindingContext.ModelState.AddModelError(modelBindingKey, exception, bindingContext.ModelMetadata);
				return;
			}

			try {
				var result = await formatter.ReadAsync(formatterContext);
				var model = result.Model;

				if (result.HasError) {
					// Formatter encountered an error. Do not use the model it returned.
					return;
				}

				if (result.IsModelSet) {
					bindingContext.Result = ModelBindingResult.Success(model);
				}
				else {
					// If the input formatter gives a "no value" result, that's always a model state error,
					// because BodyModelBinder implicitly regards input as being required for model binding.
					// If instead the input formatter wants to treat the input as optional, it must do so by
					// returning InputFormatterResult.Success(defaultForModelType), because input formatters
					// are responsible for choosing a default value for the model type.
					var message = bindingContext
							.ModelMetadata
							.ModelBindingMessageProvider
							.MissingRequestBodyRequiredValueAccessor();
					bindingContext.ModelState.AddModelError(modelBindingKey, message);
				}
			}
			catch (Exception ex) {
				bindingContext.ModelState.AddModelError(modelBindingKey, ex, bindingContext.ModelMetadata);
			}
		}

		private static void InputFormatterSelected(
					 ILogger logger,
					 IInputFormatter inputFormatter,
					 InputFormatterContext formatterContext) {
			if (logger == null) { return; }
			if (logger.IsEnabled(LogLevel.Debug)) {
				var contentType = formatterContext.HttpContext.Request.ContentType;
				_inputFormatterSelected(logger, inputFormatter, contentType, null);
			}
		}

		private static void InputFormatterRejected(
				ILogger logger,
				IInputFormatter inputFormatter,
				InputFormatterContext formatterContext) {
			if (logger == null) { return; }
			if (logger.IsEnabled(LogLevel.Debug)) {
				var contentType = formatterContext.HttpContext.Request.ContentType;
				_inputFormatterRejected(logger, inputFormatter, contentType, null);
			}
		}

		private static void NoInputFormatterSelected(
						ILogger logger,
						InputFormatterContext formatterContext) {
			if (logger.IsEnabled(LogLevel.Debug)) {
				var contentType = formatterContext.HttpContext.Request.ContentType;
				_noInputFormatterSelected(logger, contentType, null);
				if (formatterContext.HttpContext.Request.HasFormContentType) {
					var modelType = formatterContext.ModelType.FullName;
					var modelName = formatterContext.ModelName;
					_removeFromBodyAttribute(logger, modelName, modelType, null);
				}
			}
		}
	}

	public sealed class FromBodyPropertyInputFormatterContext : InputFormatterContext {

		private readonly IFromBodyPropertyModelBinderHelper _binderHelper;

		internal FromBodyPropertyInputFormatterContext(HttpContext httpContext,
						string modelName,
						ModelStateDictionary modelState,
						ModelMetadata metadata,
						Func<Stream, Encoding, TextReader> readerFactory,
						bool treatEmptyInputAsDefaultValue,
						string actionId,
						string fieldName, IFromBodyPropertyModelBinderHelper binderHelper)
			: base(httpContext, modelName, modelState, metadata, readerFactory, treatEmptyInputAsDefaultValue) {

			ActionId = actionId;
			FieldName = fieldName;
			_binderHelper = binderHelper;
		}

		public string ActionId { get; }
		public string FieldName { get; }

		public Task<Tuple<bool, object, Dictionary<string, Exception>>> ReadAsync(Func<Action<JsonSerializer, JsonReader>, Action<string, Exception>, Task> readRequestBody, Encoding encoding) {
			return _binderHelper.ReadAsync(this, readRequestBody, encoding);
		}
	}
}
