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
        Random randomNumberGen;
        bool areWeRetrying = true;
        List<WebClient> webClientsInPlay;
        int softMaxThrottleTimeInMs;

        //setup initial throttle time in milliseconds
        int throttleTime = 1;

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
            randomNumberGen = new Random();

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

            //set max throttle time to 600 seconds (10 minutes) divided by the number of requests
            lock (randomNumberGen)
            {
                softMaxThrottleTimeInMs = 600000 / urlList.Length;
            }

            //read each url
            foreach (string url in urlList)
            {
                ScheduleDownload(url);
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

        private void ScheduleDownload(string url)
        {
            //create webclient
            WebClient client = new WebClient();

            //randomise proxy selection
            int currentProxyIndex;
            lock (randomNumberGen)
            {
                currentProxyIndex = randomNumberGen.Next(numberOfProxies);
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
            client.OpenReadCompleted += GotResourceStream;

            //get address async
            client.OpenReadAsync(resourceToDownload, url);

            //add the webclient to our list of active clients
            lock (webClientsInPlay)
            {
                webClientsInPlay.Add(client);
            }
        }

        private void GotResourceStream(object sender, OpenReadCompletedEventArgs args)
        {
            //get the url as it was given to us
            string url = (string)args.UserState;
            
            //check for error
            if (args.Error == null)
            {
                //create a memory stream for this test
                MemoryStream freshDataStream = new MemoryStream();

                //copy the network stream to our memory stream
                args.Result.CopyTo(freshDataStream);

                //close the network stream
                args.Result.Close();
                args.Result.Dispose();

                //seek back to the start of the stream in memory
                freshDataStream.Position = 0;

                //get first two bytes to check for gzip magic number
                byte[] firstTwoBytes = new byte[2];
                freshDataStream.Read(firstTwoBytes, 0, 2);

                //check if they are gzips magic number
                bool gziptest = (firstTwoBytes[0] == (byte)31) && (firstTwoBytes[1] == (byte)139);

                //seek back to the start of the stream in memory
                freshDataStream.Position = 0;

                //this will store the stream we use to load the xml
                Stream streamToLoad;

                if (gziptest)
                {
                    //unzip the gzip
                    streamToLoad = new GZipStream(freshDataStream, CompressionMode.Decompress);
                }
                else
                {
                    //reference the memory stream
                    streamToLoad = freshDataStream;
                }

                //add to the results!
                lock (results)
                {
                    results.Add(url, streamToLoad);
                }

                //increment the downloads completed
                Interlocked.Increment(ref resourcesDownloaded);

                if (areWeRetrying)
                {
                    lock (randomNumberGen)
                    {
                        //only decrement approx 5% of the time
                        if (throttleTime > 0 && randomNumberGen.Next(0, 100) > 95)
                        {
                            //decrease our throttle time by 1ms
                            Interlocked.Decrement(ref throttleTime);
                        }
                    }
                }

            }
            else
            {
                //we had an error! retry

                if (areWeRetrying)
                {
                    //Console.WriteLine("Proxypool got error with URL: " + url);
                    //Console.WriteLine("Error: " + args.Error.Message);

                    //increase throttle time up to max throttle time
                    if (throttleTime < softMaxThrottleTimeInMs)
                    {
                        //lock the random number generator
                        lock (randomNumberGen)
                        {
                            //increase our throttle time
                            throttleTime = (int)Math.Round(((double)throttleTime * (randomNumberGen.NextDouble() + 2.1)), 0);

                            Console.WriteLine("Download retrying. Increasing throttle time to " + throttleTime + "ms. Maxthrottle is " + softMaxThrottleTimeInMs + "ms");
                        }

                    }

                    //throttle ourselves for throttle time
                    Thread.Sleep(throttleTime);

                    ScheduleDownload(url);

                    //Console.WriteLine("Retry fired for URL: " + url);
                }
                else
                {
                    Console.WriteLine("Proxypool got error with URL: " + url);
                    Console.WriteLine("Error: " + args.Error.Message);
                }
            }
        }
    }
}
