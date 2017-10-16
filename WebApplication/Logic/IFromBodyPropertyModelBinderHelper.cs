using Microsoft.AspNetCore.Mvc.Abstractions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;

namespace WebApplication.Logic {
	public interface IFromBodyPropertyModelBinderHelper {
		bool HasReadRequestBody { get; }
		Task<Tuple<bool, object, Dictionary<string, Exception>>> ReadAsync(FromBodyPropertyInputFormatterContext context, Func<Action<JsonSerializer, JsonReader>, Action<string, Exception>, Task> readRequestBody, Encoding encoding);
		void Initialize(string id, IEnumerable<ParameterDescriptor> parameters);
	}
}
