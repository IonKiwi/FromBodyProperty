using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
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
using Microsoft.AspNetCore.Mvc.Infrastructure;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace WebApplication.Logic {
	public class FromBodyPropertyModelBinder : IModelBinder {

		#region Framework log

		private static readonly Action<ILogger, Type, string, Exception> _attemptingToBindModel;
		private static readonly Action<ILogger, string, Type, string, Exception> _attemptingToBindParameterModel;
		private static readonly Action<ILogger, Type, string, Type, string, Exception> _attemptingToBindPropertyModel;
		private static readonly Action<ILogger, Type, string, Exception> _doneAttemptingToBindModel;
		private static readonly Action<ILogger, string, Type, Exception> _doneAttemptingToBindParameterModel;
		private static readonly Action<ILogger, Type, string, Type, Exception> _doneAttemptingToBindPropertyModel;
		private static readonly Action<ILogger, IInputFormatter, string, Exception> _inputFormatterSelected;
		private static readonly Action<ILogger, IInputFormatter, string, Exception> _inputFormatterRejected;
		private static readonly Action<ILogger, string, Exception> _noInputFormatterSelected;
		private static readonly Action<ILogger, string, string, Exception> _removeFromBodyAttribute;

		static FromBodyPropertyModelBinder() {
			_attemptingToBindModel = LoggerMessage.Define<Type, string>(
								LogLevel.Debug,
								new EventId(24, "AttemptingToBindModel"),
								"Attempting to bind model of type '{ModelType}' using the name '{ModelName}' in request data ...");
			_attemptingToBindParameterModel = LoggerMessage.Define<string, Type, string>(
							LogLevel.Debug,
							new EventId(44, "AttemptingToBindParameterModel"),
							"Attempting to bind parameter '{ParameterName}' of type '{ModelType}' using the name '{ModelName}' in request data ...");
			_attemptingToBindPropertyModel = LoggerMessage.Define<Type, string, Type, string>(
				 LogLevel.Debug,
					new EventId(13, "AttemptingToBindPropertyModel"),
				 "Attempting to bind property '{PropertyContainerType}.{PropertyName}' of type '{ModelType}' using the name '{ModelName}' in request data ...");
			_doneAttemptingToBindModel = LoggerMessage.Define<Type, string>(
							 LogLevel.Debug,
							 new EventId(25, "DoneAttemptingToBindModel"),
							 "Done attempting to bind model of type '{ModelType}' using the name '{ModelName}'.");
			_doneAttemptingToBindParameterModel = LoggerMessage.Define<string, Type>(
						 LogLevel.Debug,
							new EventId(45, "DoneAttemptingToBindParameterModel"),
						 "Done attempting to bind parameter '{ParameterName}' of type '{ModelType}'.");
			_doneAttemptingToBindPropertyModel = LoggerMessage.Define<Type, string, Type>(
						 LogLevel.Debug,
							new EventId(14, "DoneAttemptingToBindPropertyModel"),
						 "Done attempting to bind property '{PropertyContainerType}.{PropertyName}' of type '{ModelType}'.");
			_inputFormatterSelected = LoggerMessage.Define<IInputFormatter, string>(
						 LogLevel.Debug,
						 new EventId(1, "InputFormatterSelected"),
						 "Selected input formatter '{InputFormatter}' for content type '{ContentType}'.");
			_inputFormatterRejected = LoggerMessage.Define<IInputFormatter, string>(
							 LogLevel.Debug,
							 new EventId(2, "InputFormatterRejected"),
							 "Rejected input formatter '{InputFormatter}' for content type '{ContentType}'.");
			_noInputFormatterSelected = LoggerMessage.Define<string>(
							LogLevel.Debug,
							new EventId(3, "NoInputFormatterSelected"),
							"No input formatter was found to support the content type '{ContentType}' for use with the [FromBody] attribute.");
			_removeFromBodyAttribute = LoggerMessage.Define<string, string>(
								LogLevel.Debug,
								new EventId(4, "RemoveFromBodyAttribute"),
								"To use model binding, remove the [FromBody] attribute from the property or parameter named '{ModelName}' with model type '{ModelType}'.");
		}

		#endregion

		private readonly IList<IInputFormatter> _formatters;
		private readonly Func<Stream, Encoding, TextReader> _readerFactory;
		private readonly ILogger _logger;
		private readonly MvcOptions _options;

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

			AttemptingToBindModel(_logger, bindingContext);

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

				var message = "FormatUnsupportedContentType: " + httpContext.Request.ContentType;
				var exception = new UnsupportedContentTypeException(message);
				bindingContext.ModelState.AddModelError(modelBindingKey, exception, bindingContext.ModelMetadata);
				DoneAttemptingToBindModel(_logger, bindingContext);
				return;
			}

			try {
				var result = await formatter.ReadAsync(formatterContext);

				if (result.HasError) {
					// Formatter encountered an error. Do not use the model it returned.
					DoneAttemptingToBindModel(_logger, bindingContext);
					return;
				}

				if (result.IsModelSet) {
					var model = result.Model;
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
			catch (Exception exception) when (exception is InputFormatterException || ShouldHandleException(formatter)) {
				bindingContext.ModelState.AddModelError(modelBindingKey, exception, bindingContext.ModelMetadata);
			}

			DoneAttemptingToBindModel(_logger, bindingContext);
		}

		private bool ShouldHandleException(IInputFormatter formatter) {
			// Any explicit policy on the formatters overrides the default.
			var policy = (formatter as IInputFormatterExceptionPolicy)?.ExceptionPolicy ??
					InputFormatterExceptionPolicy.MalformedInputExceptions;

			return policy == InputFormatterExceptionPolicy.AllExceptions;
		}

		#region Framework log

		private static void AttemptingToBindModel(ILogger logger, ModelBindingContext bindingContext) {
			if (!logger.IsEnabled(LogLevel.Debug)) {
				return;
			}

			var modelMetadata = bindingContext.ModelMetadata;
			switch (modelMetadata.MetadataKind) {
				case ModelMetadataKind.Parameter:
					_attemptingToBindParameterModel(
							logger,
							modelMetadata.ParameterName,
							modelMetadata.ModelType,
							bindingContext.ModelName,
							null);
					break;
				case ModelMetadataKind.Property:
					_attemptingToBindPropertyModel(
							logger,
							modelMetadata.ContainerType,
							modelMetadata.PropertyName,
							modelMetadata.ModelType,
							bindingContext.ModelName,
							null);
					break;
				case ModelMetadataKind.Type:
					_attemptingToBindModel(logger, bindingContext.ModelType, bindingContext.ModelName, null);
					break;
			}
		}

		private static void DoneAttemptingToBindModel(ILogger logger, ModelBindingContext bindingContext) {
			if (!logger.IsEnabled(LogLevel.Debug)) {
				return;
			}

			var modelMetadata = bindingContext.ModelMetadata;
			switch (modelMetadata.MetadataKind) {
				case ModelMetadataKind.Parameter:
					_doneAttemptingToBindParameterModel(
							logger,
							modelMetadata.ParameterName,
							modelMetadata.ModelType,
							null);
					break;
				case ModelMetadataKind.Property:
					_doneAttemptingToBindPropertyModel(
							logger,
							modelMetadata.ContainerType,
							modelMetadata.PropertyName,
							modelMetadata.ModelType,
							null);
					break;
				case ModelMetadataKind.Type:
					_doneAttemptingToBindModel(logger, bindingContext.ModelType, bindingContext.ModelName, null);
					break;
			}
		}

		private static void InputFormatterSelected(
					 ILogger logger,
					 IInputFormatter inputFormatter,
					 InputFormatterContext formatterContext) {
			if (logger.IsEnabled(LogLevel.Debug)) {
				var contentType = formatterContext.HttpContext.Request.ContentType;
				_inputFormatterSelected(logger, inputFormatter, contentType, null);
			}
		}

		private static void InputFormatterRejected(
				ILogger logger,
				IInputFormatter inputFormatter,
				InputFormatterContext formatterContext) {
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

		#endregion
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

		public Task<(bool success, bool noValue, object model, Exception exception)> ReadAsync(Func<Func<Stream, JsonSerializerOptions, Task>, Task> readRequestBody, Encoding encoding) {
			return _binderHelper.ReadAsync(this, readRequestBody, encoding);
		}
	}
}
