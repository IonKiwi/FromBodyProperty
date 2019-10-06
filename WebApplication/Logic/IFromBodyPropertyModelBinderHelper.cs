using Microsoft.AspNetCore.Mvc.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;

namespace WebApplication.Logic {
	public interface IFromBodyPropertyModelBinderHelper {
		bool HasReadRequestBody { get; }
		Task<(bool success, bool noValue, object model, Exception exception)> ReadAsync(FromBodyPropertyInputFormatterContext context, Func<Func<Stream, JsonSerializerOptions, Task>, Task> readRequestBody, Encoding encoding);
		void Initialize(string id, IEnumerable<ParameterDescriptor> parameters);
	}
}
