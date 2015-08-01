using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ProxyPool
{
    class Test
    {
        static void Main()
        {
            //create test proxy list
            List<string> proxylist = new List<string>();
            proxylist.Add("http://127.0.0.1:3128");

            //create test URL list
            List<string> urlList = new List<string>();

            //currently using linode download test URLs
            urlList.Add("http://speedtest.frankfurt.linode.com/100MB-frankfurt.bin");
            urlList.Add("http://speedtest.singapore.linode.com/100MB-singapore.bin");
            urlList.Add("http://speedtest.tokyo.linode.com/100MB-tokyo.bin");
            urlList.Add("http://speedtest.london.linode.com/100MB-london.bin");
            urlList.Add("http://speedtest.newark.linode.com/100MB-newark.bin");
            urlList.Add("http://speedtest.atlanta.linode.com/100MB-atlanta.bin");
            urlList.Add("http://speedtest.dallas.linode.com/100MB-dallas.bin");
            urlList.Add("http://speedtest.fremont.linode.com/100MB-fremont.bin");

            //initialise proxypool with retries off and test
            Console.WriteLine("Testing ProxyPool with retries off");
            ProxyPool.Getter proxyGetter = new Getter(proxylist, false);
            proxyGetter.GetUrls(urlList.ToArray());

            //re-initialise proxypool with retries set to default on and test
            Console.WriteLine("Testing ProxyPool with retries on");
            proxyGetter = new Getter(proxylist);
            proxyGetter.GetUrls(urlList.ToArray());
        }

    }
}
