using Microsoft.AspNetCore.Mvc.Abstractions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebApplication.Logic {
	public class FromBodyPropertyModelBinderHelper : IFromBodyPropertyModelBinderHelper {

		private string _actionId;
		private readonly Dictionary<string, object> _values;
		private readonly Dictionary<string, Dictionary<string, Exception>> _errors;
		private readonly Dictionary<string, Type> _keys;

		public FromBodyPropertyModelBinderHelper() {
			Id = Guid.NewGuid().ToString("D");
			_keys = new Dictionary<string, Type>(StringComparer.Ordinal);
			_values = new Dictionary<string, object>(StringComparer.Ordinal);
			_errors = new Dictionary<string, Dictionary<string, Exception>>(StringComparer.Ordinal);
		}

		internal string Id { get; }

		public bool HasReadRequestBody {
			get;
			private set;
		}

		void IFromBodyPropertyModelBinderHelper.Initialize(string id, IEnumerable<ParameterDescriptor> parameters) {

			if (id == null) {
				throw new ArgumentNullException(nameof(id));
			}
			if (_actionId == null) {
				_actionId = id;
			}
			else if (!string.Equals(_actionId, id, StringComparison.Ordinal)) {
				throw new InvalidOperationException();
			}
			else {
				return;
			}

			foreach (ParameterDescriptor p in parameters) {
				if (_keys.TryGetValue(p.Name, out var currentType)) {
					if (currentType != p.ParameterType) {
						throw new InvalidOperationException("Duplicate parameter '" + p.Name + "' with different types.");
					}
					continue;
				}
				_keys.Add(p.Name, p.ParameterType);
			}
		}

		public async Task<Tuple<bool, object, Dictionary<string, Exception>>> ReadAsync(FromBodyPropertyInputFormatterContext context, Func<Action<JsonSerializer, JsonReader>, Action<string, Exception>, Task> readRequestBody, Encoding encoding) {

			object model = null;
			Dictionary<string, Exception> modelErrors = null;

			if (!string.Equals(_actionId, context.ActionId, StringComparison.Ordinal)) {
				throw new InvalidOperationException("Invalid action '" + context.ActionId + "', initialize for action '" + _actionId + "'.");
			}

			if (!HasReadRequestBody) {
				HasReadRequestBody = true;

				bool success = true;
				Dictionary<string, Exception> errors = null;
				await readRequestBody((s, jr) => {
					bool hasStart = false;
					while (jr.Read()) {
						var jrt = jr.TokenType;
						if (jrt == JsonToken.StartObject) {
							hasStart = true;
						}
						else if (hasStart) {
							if (jrt == JsonToken.EndObject) {
								// assert end of json
								if (jr.Read()) {
									throw new Exception("Unexpected json token '" + jr.TokenType + "', expected end of object.");
								}
								break;
							}
							else if (jrt == JsonToken.PropertyName) {
								string propertyName = (string)jr.Value;
								if (!jr.Read()) { throw new Exception("Unexpected end of object"); }

								if (!_keys.TryGetValue(propertyName, out var propertyType)) {
									throw new InvalidOperationException("Unexpected property '" + propertyName + "' found. action: " + context.ActionId);
								}

								success = true;
								errors = new Dictionary<string, Exception>(StringComparer.Ordinal);
								object currentModel = s.Deserialize(jr, propertyType);
								if (success) {
									_values.Add(propertyName, currentModel);
								}
								else {
									_errors.Add(propertyName, errors);
								}
							}
							else {
								throw new Exception("Unexpected json token '" + jr.TokenType + "'.");
							}
						}
						else {
							throw new Exception("Unexpected json token '" + jr.TokenType + "'.");
						}
					}
				},
				(path, error) => {
					if (path == null) {
						path = string.Empty;
					}
					errors.Add(path, error);
				});
			}

			if (_values.TryGetValue(context.FieldName, out model)) {
				return new Tuple<bool, object, Dictionary<string, Exception>>(true, model, null);
			}
			else if (_errors.TryGetValue(context.FieldName, out modelErrors)) {
				return new Tuple<bool, object, Dictionary<string, Exception>>(false, null, modelErrors);
			}

			return new Tuple<bool, object, Dictionary<string, Exception>>(false, null, null);
		}
	}
}
