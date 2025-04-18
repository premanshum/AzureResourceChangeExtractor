using System;
using System.Collections.Generic;
using System.Text;

namespace colonel.Policies
{
    public static class Base64Helpers
    {
        public static string Base64Encode(this string original) => original != null ? Convert.ToBase64String(Encoding.Default.GetBytes(original)) : null;
        public static string Base64Decode(this string encoded) => encoded != null ? Encoding.Default.GetString(Convert.FromBase64String(encoded)) : null;

    }
}

