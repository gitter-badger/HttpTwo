﻿using System;
using System.Threading.Tasks;
using System.Net;
using System.Linq;
using System.Threading;
using System.Collections.Specialized;
using System.Text;
using System.Security.Cryptography.X509Certificates;
using System.Net.Http;
using System.Collections.Generic;
using System.IO;

namespace HttpTwo
{
    public class Http2Client
    {
        readonly Http2Connection connection;
        readonly IStreamManager streamManager;
        IFlowControlManager flowControlManager;

        public Http2Client (Uri uri, X509CertificateCollection certificates = null, IStreamManager streamManager = null, IFlowControlManager flowControlManager = null)
            : this (new Http2ConnectionSettings (uri, certificates), streamManager, flowControlManager)
        {
        }

        public Http2Client (string url, X509CertificateCollection certificates = null, IStreamManager streamManager = null, IFlowControlManager flowControlManager = null)
            : this (new Http2ConnectionSettings (url, certificates), streamManager, flowControlManager)
        {
        }

        public Http2Client (Http2ConnectionSettings connectionSettings, IStreamManager streamManager = null, IFlowControlManager flowControlManager = null)
        {            
            this.flowControlManager = flowControlManager ?? new FlowControlManager ();
            this.streamManager = streamManager ?? new StreamManager (this.flowControlManager);
            this.ConnectionSettings = connectionSettings;

            connection = new Http2Connection (ConnectionSettings, this.streamManager, this.flowControlManager);
        }

        public Http2ConnectionSettings ConnectionSettings { get; private set; }

        public IStreamManager StreamManager { get { return streamManager; } }
        public IFlowControlManager FlowControlManager { get { return flowControlManager; } }

        public async Task Connect ()
        {
            await connection.Connect ();
        }

        public async Task<Http2Response> Post (Uri uri, NameValueCollection headers = null, byte[] data = null)
        {
            return await Send (uri, HttpMethod.Post, headers, data);
        }

        public async Task<Http2Response> Post (Uri uri, NameValueCollection headers = null, Stream data = null)
        {
            return await Send (uri, HttpMethod.Post, headers, data);
        }

        public async Task<Http2Response> Send (Uri uri, HttpMethod method, NameValueCollection headers = null, byte[] data = null)
        {
            MemoryStream ms = null;

            if (data != null)
                ms = new MemoryStream (data);
            
            return await Send (new CancellationToken (), uri, method, headers, ms);
        }

        public async Task<Http2Response> Send (Uri uri, HttpMethod method, NameValueCollection headers = null, Stream data = null)
        {
            return await Send (new CancellationToken (), uri, method, headers, data);
        }

        public async Task<Http2Response> Send (CancellationToken cancelToken, Uri uri, HttpMethod method, NameValueCollection headers = null, Stream data = null)
        {
            var semaphoreClose = new SemaphoreSlim(0);

            await connection.Connect ();

            var stream = await streamManager.Get ();
            stream.OnFrameReceived += async (frame) =>
            {
                // Check for an end of stream state
                if (stream.State == StreamState.HalfClosedRemote || stream.State == StreamState.Closed)
                    semaphoreClose.Release ();
            };

            var allHeaders = new NameValueCollection ();
            allHeaders.Add (":method", method.Method.ToUpperInvariant ());
            allHeaders.Add (":path", uri.PathAndQuery);
            allHeaders.Add (":scheme", uri.Scheme);
            allHeaders.Add (":authority", uri.Authority);
            if (headers != null && headers.Count > 0)
                allHeaders.Add (headers);

            var headerData = Util.PackHeaders (allHeaders, connection.Settings.HeaderTableSize);

            var numFrames = Math.Ceiling ((double)headerData.Length / (double)connection.Settings.MaxFrameSize);

            for (int i = 0; i < numFrames; i++) {
                // First item is headers frame, others are continuation
                IFrameContainsHeaders frame = (i == 0) ? 
                    (IFrameContainsHeaders)new HeadersFrame (stream.StreamIdentifer) 
                    : (IFrameContainsHeaders)new ContinuationFrame (stream.StreamIdentifer);

                // Set end flag if this is the last item
                if (i == numFrames - 1)
                    frame.EndHeaders = true;

                var maxFrameSize = connection.Settings.MaxFrameSize;

                var amt = maxFrameSize;
                if ( i * maxFrameSize + amt > headerData.Length)
                    amt = (uint)headerData.Length - (uint)(i * maxFrameSize);
                frame.HeaderBlockFragment = new byte[amt];
                Array.Copy (headerData, i * maxFrameSize, frame.HeaderBlockFragment, 0, amt);

                await connection.QueueFrame (frame);
            }
            // TODO: Need to send multiple frames if the frame content length is too big
            // This will require checking what the actual 

            if (data != null) {
                var sentEndOfStream = false;
                var supportsPosLength = true; // Keep track of if we threw exceptions trying pos/len of stream

                // Break stream up into data frames within allowed size
                var dataFrameBuffer = new byte[connection.Settings.MaxFrameSize];
                while (true) {

                    var rd = await data.ReadAsync (dataFrameBuffer, 0, dataFrameBuffer.Length);

                    if (rd <= 0)
                        break;
                    
                    var dataFrame = new DataFrame (stream.StreamIdentifer);
                    dataFrameBuffer.CopyTo (dataFrame.Data, 0);

                    try {
                        // See if the stream supports Length / Position to try and detect EOS
                        // we also want to see if we previously had an exception trying this
                        // and not try again if we did, since throwing exceptions every single
                        // read operation is wasteful
                        if (supportsPosLength && data.Position >= data.Length) {
                            dataFrame.EndStream = true;
                            sentEndOfStream = true;
                        }
                    } catch {
                        supportsPosLength = false;
                        sentEndOfStream = false;
                    }

                    await connection.QueueFrame (dataFrame);
                }

                // Send an empty frame with end of stream flag
                if (!sentEndOfStream)
                    await connection.QueueFrame (new DataFrame (stream.StreamIdentifer) { EndStream = true });

            }

            if (!await semaphoreClose.WaitAsync (ConnectionSettings.ConnectionTimeout, cancelToken))
                throw new TimeoutException ();

            var responseData = new List<byte> ();
            var rxHeaderData = new List<byte> ();

            foreach (var f in stream.ReceivedFrames) {
                if (f.Type == FrameType.Headers || f.Type == FrameType.Continuation) {
                    // Get the header data and add it to our buffer
                    var fch = (IFrameContainsHeaders)f;
                    if (fch.HeaderBlockFragment != null && fch.HeaderBlockFragment.Length > 0)
                        rxHeaderData.AddRange (fch.HeaderBlockFragment);    
                } else if (f.Type == FrameType.PushPromise) {
                    // TODO: In the future we need to implement PushPromise beyond grabbing header data
                    var fch = (IFrameContainsHeaders)f;
                    if (fch.HeaderBlockFragment != null && fch.HeaderBlockFragment.Length > 0)
                        rxHeaderData.AddRange (fch.HeaderBlockFragment);    
                } else if (f.Type == FrameType.Data) {
                    responseData.AddRange ((f as DataFrame).Data);
                } else if (f.Type == FrameType.GoAway) {
                    var fga = f as GoAwayFrame;
                    if (fga != null && fga.AdditionalDebugData != null && fga.AdditionalDebugData.Length > 0)
                        responseData.AddRange (fga.AdditionalDebugData);
                }
            }

            var responseHeaders = Util.UnpackHeaders (rxHeaderData.ToArray (), 
                connection.Settings.MaxHeaderListSize.HasValue ? (int)connection.Settings.MaxHeaderListSize.Value : 8192, 
                (int)connection.Settings.HeaderTableSize);

            var strStatus = "500";
            if (responseHeaders [":status"] != null)
                strStatus = responseHeaders [":status"];
            
            var statusCode = HttpStatusCode.OK;
            Enum.TryParse<HttpStatusCode> (strStatus, out statusCode);

            // Remove the stream from being tracked since we're done with it
            await streamManager.Cleanup (stream.StreamIdentifer);

            return new Http2Response {
                Status = statusCode,
                Stream = stream,
                Headers = responseHeaders,
                Body = responseData.ToArray ()
            };
        }

        public async Task<bool> Ping (byte[] opaqueData, CancellationToken cancelToken)
        {
            if (opaqueData == null || opaqueData.Length <= 0)
                throw new ArgumentNullException ("opaqueData");
            
            await connection.Connect ();

            var semaphoreWait = new SemaphoreSlim (0);
            var opaqueDataMatch = false;

            var connectionStream = await streamManager.Get (0);


            Http2Stream.FrameReceivedDelegate frameRxAction;
            frameRxAction = new Http2Stream.FrameReceivedDelegate (frame => {
                var pf = frame as PingFrame;
                if (pf != null) {
                    opaqueDataMatch = pf.Ack && pf.OpaqueData != null && pf.OpaqueData.SequenceEqual (opaqueData);
                    semaphoreWait.Release ();
                }
            });

            // Wire up the event to listen for ping response
            connectionStream.OnFrameReceived += frameRxAction;

            // Construct ping request
            var pingFrame = new PingFrame ();
            pingFrame.OpaqueData = new byte[opaqueData.Length];
            opaqueData.CopyTo (pingFrame.OpaqueData, 0);

            // Send ping
            await connection.QueueFrame (pingFrame);

            // Wait for either a ping response or timeout
            await semaphoreWait.WaitAsync (cancelToken);

            // Cleanup the event
            connectionStream.OnFrameReceived -= frameRxAction;

            return opaqueDataMatch;
        }

        public async Task<bool> Disconnect ()
        {
            return await Disconnect (Timeout.InfiniteTimeSpan);
        }

        public async Task<bool> Disconnect (TimeSpan timeout)
        {
            var connectionStream = await streamManager.Get (0);

            var semaphoreWait = new SemaphoreSlim (0);
            var cancelTokenSource = new CancellationTokenSource ();
            var sentGoAway = false;

            var sentDelegate = new Http2Stream.FrameSentDelegate (frame => {
                if (frame.Type == FrameType.GoAway) {
                    sentGoAway = true;
                    semaphoreWait.Release ();
                }
            });

            connectionStream.OnFrameSent += sentDelegate;

            connection.QueueFrame (new GoAwayFrame ());

            if (timeout != Timeout.InfiniteTimeSpan)
                cancelTokenSource.CancelAfter (timeout);
            
            await semaphoreWait.WaitAsync (cancelTokenSource.Token);

            connectionStream.OnFrameSent -= sentDelegate;

            connection.Disconnect ();

            return sentGoAway;
        }

        public class Http2Response
        {
            public HttpStatusCode Status { get; set; }
            public Http2Stream Stream { get; set; }
            public NameValueCollection Headers { get;set; }
            public byte[] Body { get;set; }
        }
    }
}
