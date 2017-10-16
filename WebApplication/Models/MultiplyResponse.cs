using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading.Tasks;

namespace WebApplication.Models {
	[DataContract]
	public class MultiplyResponse {
		[DataMember(Name = "result")]
		public int Result { get; set; }
	}
}
