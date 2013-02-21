using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Net;
using System.Threading;
using System.Collections.Concurrent;
using System.Xml;
using System.IO;
using System.IO.Compression;
using System.Net.Cache;

namespace ProxyPool
{
    class Getter
    {
        Dictionary<string, Stream> results;
        string[] proxies;
        int numberOfProxies;
        int resourcesDownloaded;
        Random randomProxySelector;
        bool areWeRetrying = true;
        List<WebClient> webClientsInPlay;

        //default is to load proxies from a text file in the running directory
        public Getter(bool retries = true)
        {
            //load list file with proxies
            List<string> proxiesList = new List<string>();

            foreach (string proxyAddress in File.ReadAllLines("proxylist.txt"))
            {
                proxiesList.Add(proxyAddress);
            }

            Setup(proxiesList, retries);

        }

        //if we already have a list of proxies to use, use that
        public Getter(List<string> proxiesList, bool retries = true)
        {
            Setup(proxiesList, retries);
        }

        //this does our setup of servicepoints and the random proxy selector etc
        private void Setup(List<string> proxiesList, bool retries)
        {
            //setup retrying
            areWeRetrying = retries;

            //load list into the array
            proxies = proxiesList.ToArray();

            //get ready to loop
            numberOfProxies = proxies.Length;

            //setup random to randomise proxy selection
            randomProxySelector = new Random();

            //setup servicepoint config
            //this will be the number of connections per proxy at any one time
            ServicePointManager.DefaultConnectionLimit = 9999;
            //we set these up for (maybe) better performance
            ServicePointManager.UseNagleAlgorithm = false;
            ServicePointManager.Expect100Continue = false;
        }

        public Dictionary<string, Stream> GetUrls(string[] urlList)
        {
            //setup
            results = new Dictionary<string, Stream>();
            resourcesDownloaded = 0;
            webClientsInPlay = new List<WebClient>();

            //read each url
            foreach (string url in urlList)
            {
                ScheduleXmldocDownload(url);
            }

            //wait for completion
            while (!Interlocked.Equals(resourcesDownloaded, urlList.Length))
            {
                //throttle ourselves to be kind
                Thread.Sleep(100);

                //if retries are off we would never finish this loop, so check to see whether all the webclients are finished
                if (!areWeRetrying)
                {
                    bool clientInProgress = false;
                    lock (webClientsInPlay)
                    {
                        foreach (WebClient client in webClientsInPlay)
                        {
                            if (client.IsBusy)
                            {
                                clientInProgress = true;
                            }
                        }
                    }

                    //no clients are still in progress, so break out of our loop
                    if (!clientInProgress)
                    {
                        break;
                    }
                }
            }

            //clean up our web clients
            foreach (WebClient client in webClientsInPlay)
            {
                client.Dispose();
            }
            webClientsInPlay = null;

            //we have now completed all downloads. return.
            return results;
        }

        private void ScheduleXmldocDownload(string url)
        {
            //create webclient
            WebClient client = new WebClient();

            //randomise proxy selection
            int currentProxyIndex;
            lock (randomProxySelector)
            {
                currentProxyIndex = randomProxySelector.Next(numberOfProxies);
            }

            //read proxy Uri from file
            Uri proxyUri = new Uri(proxies[currentProxyIndex]);

            //set proxy uri
            WebProxy proxyInUse = new WebProxy(proxyUri);
            client.Proxy = proxyInUse;

            ////set other webclient config
            //headers
            //client.Headers.Set(HttpRequestHeader.UserAgent, "User-Agent: Mozilla/5.0 (compatible; MSIE 9.0; Windows NT 6.1; WOW64; Trident/5.0)");
            //gzip header
            //client.Headers.Set(HttpRequestHeader.Accept, "text/html, application/xhtml+xml, */*");
            client.Headers.Set(HttpRequestHeader.AcceptEncoding, "gzip");
            //caching policy
            client.Headers.Set(HttpRequestHeader.CacheControl, "must-revalidate");
            client.CachePolicy = new RequestCachePolicy(RequestCacheLevel.Revalidate);
            //other

            //setup resource uri
            Uri resourceToDownload = new Uri(url);
            
            //setup method to call
            client.DownloadDataCompleted += GotResourceStream;

            //get address async
            client.DownloadDataAsync(resourceToDownload, url);

            //add the webclient to our list of active clients
            lock (webClientsInPlay)
            {
                webClientsInPlay.Add(client);
            }
        }

        private void GotResourceStream(object sender, DownloadDataCompletedEventArgs args)
        {
            //get the url as it was given to us
            string url = (string)args.UserState;
            
            //check for error
            if (args.Error == null)
            {
                //check for gzip magic number to see if we have a gzipped response
                //get first two bytes
                byte[] firstTwoBytes = new byte[2];
                Array.Copy(args.Result, firstTwoBytes, 2);

                //check if they are gzips magic number
                bool gziptest = (firstTwoBytes[0] == (byte)31) && (firstTwoBytes[1] == (byte)139);

                //this will store the stream we use to load the xml
                Stream streamToLoad;

                if (gziptest)
                {
                    //setup memory stream
                    MemoryStream streamInMemory = new MemoryStream(args.Result);

                    //unzip the gzip
                    streamToLoad = new GZipStream(streamInMemory, CompressionMode.Decompress);
                }
                else
                {
                    //setup memory stream
                    streamToLoad = new MemoryStream(args.Result);
                }

                //add to the results!
                lock (results)
                {
                    results.Add(url, streamToLoad);
                }

                //increment the downloads completed
                Interlocked.Increment(ref resourcesDownloaded);
            }
            else
            {
                //we had an error! retry
                //WebClient senderClient = (WebClient)sender;
                //Console.WriteLine("ProxyPool request failed using proxy: " + senderClient.Proxy.GetProxy(new Uri(url)));
                //Console.WriteLine("ProxyPool Retrying: " + url);
                if (areWeRetrying)
                {
                    ScheduleXmldocDownload(url);
                }
            }
        }
    }
}
