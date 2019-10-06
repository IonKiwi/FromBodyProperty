using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Formatters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Net.Http.Headers;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebApplication.Logic {
	public class JsonInputFormatter : TextInputFormatter, IInputFormatterExceptionPolicy {

		private static readonly MediaTypeHeaderValue ApplicationJson = MediaTypeHeaderValue.Parse("application/json").CopyAsReadOnly();
		private static readonly MediaTypeHeaderValue TextJson = MediaTypeHeaderValue.Parse("text/json").CopyAsReadOnly();

		private readonly ILogger _logger;

		public JsonInputFormatter(JsonOptions options, ILogger<SystemTextJsonInputFormatter> logger) {

			SerializerOptions = options.JsonSerializerOptions;
			_logger = logger;

			SupportedEncodings.Add(UTF8EncodingWithoutBOM);
			SupportedMediaTypes.Add(ApplicationJson);
			SupportedMediaTypes.Add(TextJson);
		}

		public JsonSerializerOptions SerializerOptions { get; }

		InputFormatterExceptionPolicy IInputFormatterExceptionPolicy.ExceptionPolicy => InputFormatterExceptionPolicy.MalformedInputExceptions;

		public sealed override async Task<InputFormatterResult> ReadRequestBodyAsync(
						InputFormatterContext context,
						Encoding encoding) {
			if (context == null) {
				throw new ArgumentNullException(nameof(context));
			}

			if (encoding == null) {
				throw new ArgumentNullException(nameof(encoding));
			}

			if (encoding.CodePage != Encoding.UTF8.CodePage) {
				throw new NotSupportedException(encoding.EncodingName);
			}

			var httpContext = context.HttpContext;
			var inputStream = httpContext.Request.Body;

			if (context is FromBodyPropertyInputFormatterContext bodyPropertyContext) {

				Func<Func<Stream, JsonSerializerOptions, Task>, Task> readRequestBody = async (readAction) => {
					await readAction(inputStream, SerializerOptions);
				};

				var result = await bodyPropertyContext.ReadAsync(readRequestBody, encoding);
				if (result.success) {
					if (result.noValue && !context.TreatEmptyInputAsDefaultValue) {
						// Some nonempty inputs might deserialize as null, for example whitespace,
						// or the JSON-encoded value "null". The upstream BodyModelBinder needs to
						// be notified that we don't regard this as a real input so it can register
						// a model binding error.
						return InputFormatterResult.NoValue();
					}
					Log.JsonInputSuccess(_logger, context.ModelType);
					return InputFormatterResult.Success(result.model);
				}
				else if (result.exception is JsonException jsonException) {
					var path = jsonException.Path;

					var formatterException = new InputFormatterException(jsonException.Message, jsonException);

					context.ModelState.TryAddModelError(path, formatterException, context.Metadata);

					Log.JsonInputException(_logger, jsonException);

					return InputFormatterResult.Failure();
				}
				else {
					var ex = result.exception ?? new Exception("Failed to read model");
					throw ex;
				}
			}
			else {
				object model;
				try {
					model = await JsonSerializer.DeserializeAsync(inputStream, context.ModelType, SerializerOptions);
				}
				catch (JsonException jsonException) {
					var path = jsonException.Path;

					var formatterException = new InputFormatterException(jsonException.Message, jsonException);

					context.ModelState.TryAddModelError(path, formatterException, context.Metadata);

					Log.JsonInputException(_logger, jsonException);

					return InputFormatterResult.Failure();
				}

				if (model == null && !context.TreatEmptyInputAsDefaultValue) {
					// Some nonempty inputs might deserialize as null, for example whitespace,
					// or the JSON-encoded value "null". The upstream BodyModelBinder needs to
					// be notified that we don't regard this as a real input so it can register
					// a model binding error.
					return InputFormatterResult.NoValue();
				}
				else {
					Log.JsonInputSuccess(_logger, context.ModelType);
					return InputFormatterResult.Success(model);
				}
			}
		}

		private static class Log {
			private static readonly Action<ILogger, string, Exception> _jsonInputFormatterException;
			private static readonly Action<ILogger, string, Exception> _jsonInputSuccess;

			static Log() {
				_jsonInputFormatterException = LoggerMessage.Define<string>(
						LogLevel.Debug,
						new EventId(1, "SystemTextJsonInputException"),
						"JSON input formatter threw an exception: {Message}");
				_jsonInputSuccess = LoggerMessage.Define<string>(
						LogLevel.Debug,
						new EventId(2, "SystemTextJsonInputSuccess"),
						"JSON input formatter succeeded, deserializing to type '{TypeName}'");
			}

			public static void JsonInputException(ILogger logger, Exception exception)
					=> _jsonInputFormatterException(logger, exception.Message, exception);

			public static void JsonInputSuccess(ILogger logger, Type modelType)
					=> _jsonInputSuccess(logger, modelType.FullName, null);
		}
	}
}
