using System;
using System.Configuration;

namespace Gigya.Microdot.Ninject
{
   
    /// <summary>
    /// Notice that REGEX_DEFAULT_MATCH_TIMEOUT can be set only once and will be determined when calling the first regex, the default is infinite!!
    /// </summary>
    public class RegexTimeoutInitializer 
    {
        public void Init()
        {
            int regexDefaultMatchTimeOutMs = (int)TimeSpan.FromSeconds(10).TotalMilliseconds;
            try
            {
                var strValue = ConfigurationManager.AppSettings["regexDefaultMachTimeOutMs"];
                if(strValue != null)
                    regexDefaultMatchTimeOutMs = int.Parse(strValue);
            }
            catch (Exception)
            {
                // there is nothing to do about
            }

            AppDomain.CurrentDomain.SetData("REGEX_DEFAULT_MATCH_TIMEOUT", TimeSpan.FromMilliseconds(regexDefaultMatchTimeOutMs));
            Console.WriteLine($"REGEX_DEFAULT_MATCH_TIMEOUT is set to {regexDefaultMatchTimeOutMs} ms");
        }
    }
}