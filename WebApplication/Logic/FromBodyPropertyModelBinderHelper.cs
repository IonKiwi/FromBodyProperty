using Microsoft.AspNetCore.Mvc.Abstractions;
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace WebApplication.Logic {
	public class FromBodyPropertyModelBinderHelper : IFromBodyPropertyModelBinderHelper {

		private string _actionId;
		private readonly Dictionary<string, byte[]> _values;
		private readonly Dictionary<string, Type> _keys;
		private JsonSerializerOptions _options;

		public FromBodyPropertyModelBinderHelper() {
			Id = Guid.NewGuid().ToString("D");
			_keys = new Dictionary<string, Type>(StringComparer.Ordinal);
			_values = new Dictionary<string, byte[]>(StringComparer.Ordinal);
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

		private enum DataBlockMode {
			Start,
			Body,
			Property,
			SkipObject,
			SkipArray,
			End,
		}

		private (bool handled, int consumed) HandleDataBlock(FromBodyPropertyInputFormatterContext context, ref DataBlockMode blockMode, ref int depth, ref JsonReaderState readerState, ReadOnlySpan<byte> data, bool finalBlock, ref string currentProperty, Dictionary<string, List<byte[]>> propertyData) {
			var jr = new Utf8JsonReader(data, finalBlock, readerState);

			int start = 0;
			while (jr.Read()) {
				var jrt = jr.TokenType;

				if (blockMode == DataBlockMode.Start) {
					if (jrt == JsonTokenType.StartObject) {
						blockMode = DataBlockMode.Body;
					}
					else if (jrt == JsonTokenType.Comment) {
						continue;
					}
					else {
						throw new Exception("Unexpected json token '" + jrt + "'.");
					}
				}
				else if (blockMode == DataBlockMode.End) {
					if (jrt == JsonTokenType.Comment) {
						continue;
					}
					else {
						throw new Exception("Unexpected json token '" + jr.TokenType + "', expected end of object.");
					}
				}
				else if (blockMode == DataBlockMode.Body) {
					if (jrt == JsonTokenType.EndObject) {
						blockMode = DataBlockMode.End;
					}
					else if (jrt == JsonTokenType.Comment) {
						continue;
					}
					else if (jrt == JsonTokenType.PropertyName) {
						currentProperty = jr.GetString();
						if (propertyData.ContainsKey(currentProperty)) {
							throw new Exception("Duplicate property: " + currentProperty);
						}
						propertyData.Add(currentProperty, new List<byte[]>());
						start = checked((int)jr.BytesConsumed);
						blockMode = DataBlockMode.Property;
					}
					else {
						throw new Exception("Unexpected json token '" + jr.TokenType + "', expected property.");
					}
				}
				else if (blockMode == DataBlockMode.Property) {
					if (IsValueToken(jrt)) {
						propertyData[currentProperty].Add(data.Slice(start, checked((int)jr.BytesConsumed) - start).ToArray());
						blockMode = DataBlockMode.Body;
						currentProperty = null;
						start = checked((int)jr.BytesConsumed);
					}
					else if (jrt == JsonTokenType.Comment) {
						continue;
					}
					else if (jrt == JsonTokenType.StartObject) {
						depth = jr.CurrentDepth;
						blockMode = DataBlockMode.SkipObject;
					}
					else if (jrt == JsonTokenType.StartArray) {
						depth = jr.CurrentDepth;
						blockMode = DataBlockMode.SkipArray;
					}
					else {
						throw new Exception("Unexpected json token '" + jr.TokenType + "', expected property value.");
					}
				}
				else if (blockMode == DataBlockMode.SkipObject) {
					if (jrt == JsonTokenType.EndObject && jr.CurrentDepth == depth) {
						propertyData[currentProperty].Add(data.Slice(start, checked((int)jr.BytesConsumed) - start).ToArray());
						blockMode = DataBlockMode.Body;
						currentProperty = null;
						start = checked((int)jr.BytesConsumed);
					}
				}
				else if (blockMode == DataBlockMode.SkipArray) {
					if (jrt == JsonTokenType.EndArray && jr.CurrentDepth == depth) {
						propertyData[currentProperty].Add(data.Slice(start, checked((int)jr.BytesConsumed) - start).ToArray());
						blockMode = DataBlockMode.Body;
						currentProperty = null;
						start = checked((int)jr.BytesConsumed);
					}
				}
				else {
					throw new Exception("Unexpected state");
				}
			}

			if (!jr.IsFinalBlock) {
				readerState = jr.CurrentState;
				var consumed = checked((int)jr.BytesConsumed);
				if (consumed - start > 0 && (blockMode == DataBlockMode.SkipObject || blockMode == DataBlockMode.SkipArray)) {
					propertyData[currentProperty].Add(data.Slice(start, consumed - start).ToArray());
				}
				return (false, consumed);
			}

			if (blockMode != DataBlockMode.End) {
				throw new Exception("More data expected");
			}

			return (true, 0);
		}

		private static bool IsValueToken(JsonTokenType token) {
			return token == JsonTokenType.String || token == JsonTokenType.Number || token == JsonTokenType.True || token == JsonTokenType.False || token == JsonTokenType.Null;
		}

		public async Task<(bool success, bool noValue, object model, Exception exception)> ReadAsync(FromBodyPropertyInputFormatterContext context, Func<Func<Stream, JsonSerializerOptions, Task>, Task> readRequestBody, Encoding encoding) {

			if (!string.Equals(_actionId, context.ActionId, StringComparison.Ordinal)) {
				throw new InvalidOperationException("Invalid action '" + context.ActionId + "', initialize for action '" + _actionId + "'.");
			}

			if (!HasReadRequestBody) {
				HasReadRequestBody = true;
				await readRequestBody(async (s, options) => {
					_options = options;
					int bufferSize = 0x4096;
					var propertyData = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
					var readerOptions = new JsonReaderOptions {
						AllowTrailingCommas = options.AllowTrailingCommas,
						CommentHandling = options.ReadCommentHandling,
						MaxDepth = options.MaxDepth
					};
					var readerState = new JsonReaderState(readerOptions);
					var buffer = ArrayPool<byte>.Shared.Rent(bufferSize);
					Memory<byte> memory = buffer.AsMemory(0, bufferSize);
					int length;
					int offset = 0;
					string currentProperty = null;
					int depth = 0;
					DataBlockMode blockMode = DataBlockMode.Start;
					bool handled = false;
					bool finalBlock = false;
					int consumed = 0;
					try {
						length = await s.ReadAsync(memory);
						finalBlock = length == 0;
						do {
							(handled, consumed) = HandleDataBlock(context, ref blockMode, ref depth, ref readerState, memory.Span.Slice(offset, length), finalBlock, ref currentProperty, propertyData);
							offset += consumed;
							if (!handled) {
								int bytesInBuffer = length - offset;
								if (bytesInBuffer == 0) {
									// read more data
									length = await s.ReadAsync(memory);
									finalBlock = length == 0;
									offset = 0;
								}
								else if ((uint)bytesInBuffer > ((uint)bufferSize / 2)) {
									// expand buffer
									bufferSize = (bufferSize < (int.MaxValue / 2)) ? bufferSize * 2 : int.MaxValue;
									var buffer2 = ArrayPool<byte>.Shared.Rent(bufferSize);

									// copy the unprocessed data
									Buffer.BlockCopy(buffer, offset, buffer2, 0, bytesInBuffer);

									// return previous buffer
									ArrayPool<byte>.Shared.Return(buffer);
									buffer = buffer2;
									memory = buffer.AsMemory(0, bufferSize);

									// read more data
									length = await s.ReadAsync(memory.Slice(bytesInBuffer));
									finalBlock = length == 0;
									length += bytesInBuffer;
									offset = 0;
								}
								else {
									Buffer.BlockCopy(buffer, offset, buffer, 0, bytesInBuffer);

									// read more data
									length = await s.ReadAsync(memory.Slice(bytesInBuffer));
									finalBlock = length == 0;
									length += bytesInBuffer;
									offset = 0;
								}
							}
						}
						while (!handled);
					}
					finally {
						ArrayPool<byte>.Shared.Return(buffer);
					}

					foreach ((var key, var value) in propertyData) {
						_values.Add(key, value.SelectMany(z => z).ToArray());
					}
				});
			}

			if (!_keys.TryGetValue(context.FieldName, out var propertyType)) {
				throw new InvalidOperationException("Unknown property '" + context.FieldName + "' requested.");
			}
			else if (_values.TryGetValue(context.FieldName, out var propertyData)) {
				try {
					var v = JsonSerializer.Deserialize(propertyData, propertyType, _options);
					return (true, false, v, null);
				}
				catch (Exception ex) {
					return (false, false, null, ex);
				}
			}

			return (true, true, null, null);
		}
	}
}
