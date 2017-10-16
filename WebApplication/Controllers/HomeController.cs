using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using WebApplication.Models;
using WebApplication.Logic;

namespace WebApplication.Controllers {
	public class HomeController : Controller {
		public IActionResult Index() {
			return View();
		}

		public MultiplyResponse Multiply([FromBodyProperty] int x, [FromBodyProperty] int y) {
			return new MultiplyResponse() { Result = x * y };
		}

		public IActionResult Error() {
			return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
		}
	}
}
