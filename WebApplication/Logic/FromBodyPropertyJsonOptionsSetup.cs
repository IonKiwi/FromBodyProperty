using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.ObjectPool;
using Microsoft.Extensions.Options;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace WebApplication.Logic {
	public class FromBodyPropertyJsonOptionsSetup : IConfigureOptions<MvcOptions> {
		private readonly ILoggerFactory _loggerFactory;
		private readonly JsonOptions _jsonOptions;

		public FromBodyPropertyJsonOptionsSetup(
				ILoggerFactory loggerFactory,
				IOptions<JsonOptions> jsonOptions) {
			if (loggerFactory == null) {
				throw new ArgumentNullException(nameof(loggerFactory));
			}

			if (jsonOptions == null) {
				throw new ArgumentNullException(nameof(jsonOptions));
			}

			_loggerFactory = loggerFactory;
			_jsonOptions = jsonOptions.Value;
		}

		public void Configure(MvcOptions options) {
			for (var i = options.InputFormatters.Count - 1; i >= 0; i--) {
				if (options.InputFormatters[i] is global::Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter) {
					var jsonInputLogger = _loggerFactory.CreateLogger<global::Microsoft.AspNetCore.Mvc.Formatters.SystemTextJsonInputFormatter>();
					options.InputFormatters[i] = new JsonInputFormatter(
							_jsonOptions,
							jsonInputLogger);
				}
			}
		}
	}
}
