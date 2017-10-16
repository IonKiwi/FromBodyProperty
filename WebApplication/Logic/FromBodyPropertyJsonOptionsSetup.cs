using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace WebApplication.Logic {
	public class FromBodyPropertyJsonOptionsSetup : IConfigureOptions<MvcOptions> {
		private readonly ILoggerFactory _loggerFactory;
		private readonly JsonSerializerSettings _jsonSerializerSettings;
		private readonly ArrayPool<char> _charPool;
		private readonly ObjectPoolProvider _objectPoolProvider;

		public FromBodyPropertyJsonOptionsSetup(
				ILoggerFactory loggerFactory,
				IOptions<MvcJsonOptions> jsonOptions,
				ArrayPool<char> charPool,
				ObjectPoolProvider objectPoolProvider) {
			if (loggerFactory == null) {
				throw new ArgumentNullException(nameof(loggerFactory));
			}

			if (jsonOptions == null) {
				throw new ArgumentNullException(nameof(jsonOptions));
			}

			if (charPool == null) {
				throw new ArgumentNullException(nameof(charPool));
			}

			if (objectPoolProvider == null) {
				throw new ArgumentNullException(nameof(objectPoolProvider));
			}

			_loggerFactory = loggerFactory;
			_jsonSerializerSettings = jsonOptions.Value.SerializerSettings;
			_charPool = charPool;
			_objectPoolProvider = objectPoolProvider;
		}

		public void Configure(MvcOptions options) {
			for (var i = options.InputFormatters.Count - 1; i >= 0; i--) {
				if (options.InputFormatters[i] is global::Microsoft.AspNetCore.Mvc.Formatters.JsonInputFormatter) {
					var jsonInputLogger = _loggerFactory.CreateLogger<JsonInputFormatter>();
					options.InputFormatters[i] = new JsonInputFormatter(
							jsonInputLogger,
							_jsonSerializerSettings,
							_charPool,
							_objectPoolProvider,
							options.SuppressInputFormatterBuffering);
				}
			}
		}
	}
}
