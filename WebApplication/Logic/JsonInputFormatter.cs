using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Internal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.Formatters.Json.Internal;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Net.Http.Headers;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebApplication.Logic {
	public class JsonInputFormatter : global::Microsoft.AspNetCore.Mvc.Formatters.JsonInputFormatter {

		private static readonly Action<ILogger, Exception> _jsonInputFormatterCrashed;

		private readonly IArrayPool<char> _charPool;
		private readonly ILogger _logger;
		private readonly MvcOptions _options;
		private readonly MvcJsonOptions _jsonOptions;

		static JsonInputFormatter() {
			_jsonInputFormatterCrashed = LoggerMessage.Define(
								LogLevel.Debug,
								1,
								"JSON input formatter threw an exception.");
		}

		//
		// Summary:
		//     Initializes a new instance of Microsoft.AspNetCore.Mvc.Formatters.JsonInputFormatter.
		//
		// Parameters:
		//   logger:
		//     The Microsoft.Extensions.Logging.ILogger.
		//
		//   serializerSettings:
		//     The Newtonsoft.Json.JsonSerializerSettings. Should be either the application-wide
		//     settings (Microsoft.AspNetCore.Mvc.MvcJsonOptions.SerializerSettings) or an instance
		//     Microsoft.AspNetCore.Mvc.Formatters.JsonSerializerSettingsProvider.CreateSerializerSettings
		//     initially returned.
		//
		//   charPool:
		//     The System.Buffers.ArrayPool`1.
		//
		//   objectPoolProvider:
		//     The Microsoft.Extensions.ObjectPool.ObjectPoolProvider.
		//
		//   options:
		//     The Microsoft.AspNetCore.Mvc.MvcOptions.
		//
		//   jsonOptions:
		//     The Microsoft.AspNetCore.Mvc.MvcJsonOptions.
		public JsonInputFormatter(ILogger logger, JsonSerializerSettings serializerSettings, ArrayPool<char> charPool, ObjectPoolProvider objectPoolProvider, MvcOptions options, MvcJsonOptions jsonOptions)
			: base(logger, serializerSettings, charPool, objectPoolProvider, options, jsonOptions) {
			_logger = logger;
			_charPool = new JsonArrayPool<char>(charPool);
			_options = options;
			_jsonOptions = jsonOptions;
		}

		public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context, Encoding encoding) {

			if (context == null) {
				throw new ArgumentNullException(nameof(context));
			}

			if (encoding == null) {
				throw new ArgumentNullException(nameof(encoding));
			}

			if (context is FromBodyPropertyInputFormatterContext) {
				var myContext = (FromBodyPropertyInputFormatterContext)context;

				Func<Action<JsonSerializer, JsonReader>, Action<string, Exception>, Task> readRequestBody = async (readAction, errorAction) => {

					var request = context.HttpContext.Request;

					var suppressInputFormatterBuffering = _options.SuppressInputFormatterBuffering;

					if (!request.Body.CanSeek && !suppressInputFormatterBuffering) {
						// JSON.Net does synchronous reads. In order to avoid blocking on the stream, we asynchronously
						// read everything into a buffer, and then seek back to the beginning.
						request.EnableBuffering();
						Debug.Assert(request.Body.CanSeek);

						await request.Body.DrainAsync(CancellationToken.None);
						request.Body.Seek(0L, SeekOrigin.Begin);
					}

					using (var streamReader = context.ReaderFactory(request.Body, encoding)) {
						using (var jsonReader = new JsonTextReader(streamReader)) {
							jsonReader.ArrayPool = _charPool;
							jsonReader.CloseInput = false;

							void ErrorHandler(object sender, Newtonsoft.Json.Serialization.ErrorEventArgs eventArgs) {

								var path = eventArgs.ErrorContext.Path;
								var ex = eventArgs.ErrorContext.Error;
								errorAction(path, ex);

								JsonInputException(_logger, eventArgs.ErrorContext.Error);

								// Error must always be marked as handled
								// Failure to do so can cause the exception to be rethrown at every recursive level and
								// overflow the stack for x64 CLR processes
								eventArgs.ErrorContext.Handled = true;
							}

							var type = context.ModelType;
							var jsonSerializer = CreateJsonSerializer();
							jsonSerializer.Error += ErrorHandler;
							try {
								readAction(jsonSerializer, jsonReader);
							}
							finally {
								// Clean up the error handler since CreateJsonSerializer() pools instances.
								jsonSerializer.Error -= ErrorHandler;
								ReleaseJsonSerializer(jsonSerializer);
							}
						}
					}
				};

				var (success, model, modelErrors) = await myContext.ReadAsync(readRequestBody, encoding);
				if (success) {
					if (model == null && !context.TreatEmptyInputAsDefaultValue) {
						// Some nonempty inputs might deserialize as null, for example whitespace,
						// or the JSON-encoded value "null". The upstream BodyModelBinder needs to
						// be notified that we don't regard this as a real input so it can register
						// a model binding error.
						return InputFormatterResult.NoValue();
					}
					else {
						return InputFormatterResult.Success(model);
					}
				}

				foreach ((var path, var error) in modelErrors) {
					// Handle path combinations such as "" + "Property", "Parent" + "Property", or "Parent" + "[12]".
					var key = path;
					if (!string.IsNullOrEmpty(context.ModelName)) {
						if (string.IsNullOrEmpty(path)) {
							key = context.ModelName;
						}
						else if (path[0] == '[') {
							key = context.ModelName + path;
						}
						else {
							key = context.ModelName + "." + path;
						}
					}

					var metadata = GetPathMetadata(context.Metadata, path);
					var modelStateException = WrapExceptionForModelState(error);
					context.ModelState.TryAddModelError(key, modelStateException, metadata);

					if (!(error is JsonException || error is OverflowException)) {
						var exceptionDispatchInfo = ExceptionDispatchInfo.Capture(error);
						exceptionDispatchInfo.Throw();
					}
				}

				return InputFormatterResult.Failure();
			}

			return await base.ReadRequestBodyAsync(context, encoding);
		}

		private ModelMetadata GetPathMetadata(ModelMetadata metadata, string path) {
			var index = 0;
			while (index >= 0 && index < path.Length) {
				if (path[index] == '[') {
					// At start of "[0]".
					if (metadata.ElementMetadata == null) {
						// Odd case but don't throw just because ErrorContext had an odd-looking path.
						break;
					}

					metadata = metadata.ElementMetadata;
					index = path.IndexOf(']', index);
				}
				else if (path[index] == '.' || path[index] == ']') {
					// Skip '.' in "prefix.property" or "[0].property" or ']' in "[0]".
					index++;
				}
				else {
					// At start of "property", "property." or "property[0]".
					var endIndex = path.IndexOfAny(new[] { '.', '[' }, index);
					if (endIndex == -1) {
						endIndex = path.Length;
					}

					var propertyName = path.Substring(index, endIndex - index);
					if (metadata.Properties[propertyName] == null) {
						// Odd case but don't throw just because ErrorContext had an odd-looking path.
						break;
					}

					metadata = metadata.Properties[propertyName];
					index = endIndex;
				}
			}

			return metadata;
		}

		private Exception WrapExceptionForModelState(Exception exception) {
			// In 2.0 and earlier we always gave a generic error message for errors that come from JSON.NET
			// We only allow it in 2.1 and newer if the app opts-in.
			if (!(_jsonOptions.AllowInputFormatterExceptionMessages)) {
				// This app is not opted-in to JSON.NET messages, return the original exception.
				return exception;
			}

			// It's not known that Json.NET currently ever raises error events with exceptions
			// other than these two types, but we're being conservative and limiting which ones
			// we regard as having safe messages to expose to clients
			if (exception is JsonReaderException || exception is JsonSerializationException) {
				// InputFormatterException specifies that the message is safe to return to a client, it will
				// be added to model state.
				return new InputFormatterException(exception.Message, exception);
			}

			// Not a known exception type, so we're not going to assume that it's safe.
			return exception;
		}

		private static void JsonInputException(ILogger logger, Exception exception) {
			_jsonInputFormatterCrashed(logger, exception);
		}
	}
}
