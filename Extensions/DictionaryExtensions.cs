using System;
using System.Collections.Generic;
using System.Text;

namespace Microsoft.Exchange.WebServices
{
    public static class DictionaryExtensions
    {
        public static void AddOrUpdate<T1, T2>(this Dictionary<T1, T2> dic, T1 key, T2 value)
        {
            if (dic.ContainsKey(key))
                dic[key] = value;
            else
                dic.Add(key, value);
        }
    }
}
