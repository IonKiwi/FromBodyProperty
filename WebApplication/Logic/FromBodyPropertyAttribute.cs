using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace WebApplication.Logic
{
	public class FromBodyPropertyAttribute : ModelBinderAttribute {
		public FromBodyPropertyAttribute() {
			BindingSource = BindingSource.Custom;
			BinderType = typeof(FromBodyPropertyModelBinder);
		}
	}
}
