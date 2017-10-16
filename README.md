FromBodyProperty
=======================

FromBodyPropertyAttribute lets you bind ASP.NET Core MVC Controller action parameters from raw request body json content properties.

How to use
----------------------------

## Setup
In Startup.cs add (I)FromBodyPropertyModelBinderHelper & FromBodyPropertyJsonOptionsSetup

```csharp
		public void ConfigureServices(IServiceCollection services) {
			services.AddMvc();
			services.AddScoped<IFromBodyPropertyModelBinderHelper, FromBodyPropertyModelBinderHelper>();
			services.TryAddEnumerable(ServiceDescriptor.Transient<IConfigureOptions<MvcOptions>, FromBodyPropertyJsonOptionsSetup>());
		}
```

## Example controller

```csharp
	public class HomeController : Controller {
		public MultiplyResponse Multiply([FromBodyProperty] int x, [FromBodyProperty] int y) {
			return new MultiplyResponse() { Result = x * y };
		}
	}
```

## Example ajax request

```javascript
		var request = {
			x: 2,
			y: 5
		}

		var ajaxOptions = {
			url: "/Home/Multiply",
			dataType: "json",
			contentType: "application/json; charset=utf-8",
			type: "POST",
			data: JSON.stringify(request),
			success: function (data, textStatus, jqXHR) {
				alert(JSON.stringify(data, null, 4));
			},
			error: function (jqXHR, textStatus, errorThrown) {
				if (jqXHR.responseJSON) {
					alert(JSON.stringify(jqXHR.responseJSON, null, 4));
				}
				alert(textStatus);
			}
		}
		jQuery.ajax(ajaxOptions);
```

## License
[Apache 2.0](LICENSE)
