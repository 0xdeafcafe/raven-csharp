﻿#region License

// Copyright (c) 2014 The Sentry Team and individual contributors.
// All rights reserved.
// 
// Redistribution and use in source and binary forms, with or without modification, are permitted
// provided that the following conditions are met:
// 
//     1. Redistributions of source code must retain the above copyright notice, this list of
//        conditions and the following disclaimer.
// 
//     2. Redistributions in binary form must reproduce the above copyright notice, this list of
//        conditions and the following disclaimer in the documentation and/or other materials
//        provided with the distribution.
// 
//     3. Neither the name of the Sentry nor the names of its contributors may be used to
//        endorse or promote products derived from this software without specific prior written
//        permission.
// 
// THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR
// IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND
// FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR
// CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL
// DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE,
// DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY,
// WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN
// ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

#endregion

#if !(net40)

using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

using Newtonsoft.Json;

using SharpRaven.Utilities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using SharpRaven.Data;

namespace SharpRaven
{
    /// <summary>
    /// The Raven Client, responsible for capturing exceptions and sending them to Sentry.
    /// </summary>
    public partial class RavenClient
    {
        /// <summary>
        /// Captures the <see cref="Exception" />.
        /// </summary>
        /// <param name="exception">The <see cref="Exception" /> to capture.</param>
        /// <param name="message">The optional messge to capture. Default: <see cref="Exception.Message" />.</param>
        /// <param name="level">The <see cref="ErrorLevel" /> of the captured <paramref name="exception" />. Default: <see cref="ErrorLevel.Error"/>.</param>
        /// <param name="tags">The tags to annotate the captured <paramref name="exception" /> with.</param>
        /// <param name="fingerprint">The custom fingerprint to annotate the captured <paramref name="message" /> with.</param>
        /// <param name="extra">The extra metadata to send with the captured <paramref name="exception" />.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID" /> of the successfully captured <paramref name="exception" />, or <c>null</c> if it fails.
        /// </returns>
        public async Task<string> CaptureExceptionAsync(Exception exception,
                                                        SentryMessage message = null,
                                                        ErrorLevel level = ErrorLevel.Error,
                                                        IDictionary<string, string> tags = null,
                                                        string[] fingerprint = null,
                                                        object extra = null)
        {
            JsonPacket packet = this.jsonPacketFactory.Create(CurrentDsn.ProjectID,
                                                              exception,
                                                              message,
                                                              level,
                                                              tags,
                                                              fingerprint,
                                                              extra);

            return await SendAsync(packet, CurrentDsn);
        }


        /// <summary>
        /// Captures the message.
        /// </summary>
        /// <param name="message">The message to capture.</param>
        /// <param name="level">The <see cref="ErrorLevel" /> of the captured <paramref name="message"/>. Default <see cref="ErrorLevel.Info"/>.</param>
        /// <param name="tags">The tags to annotate the captured <paramref name="message"/> with.</param>
        /// <param name="fingerprint">The custom fingerprint to annotate the captured <paramref name="message" /> with.</param>
        /// <param name="extra">The extra metadata to send with the captured <paramref name="message"/>.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID"/> of the successfully captured <paramref name="message"/>, or <c>null</c> if it fails.
        /// </returns>
        public async Task<string> CaptureMessageAsync(SentryMessage message,
                                                      ErrorLevel level = ErrorLevel.Info,
                                                      Dictionary<string, string> tags = null,
                                                      string[] fingerprint = null,
                                                      object extra = null)
        {
            JsonPacket packet = this.jsonPacketFactory.Create(CurrentDsn.ProjectID, message, level, tags, fingerprint, extra);

            return await SendAsync(packet, CurrentDsn);
        }


        /// <summary>
        /// Sends the specified packet to Sentry.
        /// </summary>
        /// <param name="packet">The packet to send.</param>
        /// <param name="dsn">The Data Source Name in Sentry.</param>
        /// <returns>
        /// The <see cref="JsonPacket.EventID"/> of the successfully captured JSON packet, or <c>null</c> if it fails.
        /// </returns>
        protected virtual async Task<string> SendAsync(JsonPacket packet, Dsn dsn)
        {
            try
            {
                packet = PreparePacket(packet);

                // TODO: HttpClient's constructor is locking shared (static) resources due to logging. Refactor this so it doesn't kill performance. @asbjornu
                using (var client = new HttpClient { Timeout = Timeout })
                {
                    var userAgent = new ProductInfoHeaderValue(PacketBuilder.ProductName, PacketBuilder.ProductVersion);
                    client.DefaultRequestHeaders.UserAgent.Add(userAgent);
                    client.DefaultRequestHeaders.Accept.Add(MediaTypeWithQualityHeaderValue.Parse("application/json"));
                    client.DefaultRequestHeaders.Add("X-Sentry-Auth", PacketBuilder.CreateAuthenticationHeader(dsn));
                    var data = packet.ToString(Formatting.None);

                    if (LogScrubber != null)
                        data = LogScrubber.Scrub(data);

                    HttpContent content = new StringContent(data);

                    try
                    {
                        if (Compression)
                            content = new CompressedContent(content, "gzip");

                        using (var response = await client.PostAsync(dsn.SentryUri, content))
                        {
                            var responseContent = await response.Content.ReadAsStringAsync();
                            if (responseContent == null)
                                return null;

                            var responseJson = JsonConvert.DeserializeObject<dynamic>(responseContent);
                            return responseJson.id;
                        }
                    }
                    finally
                    {
                        content.Dispose();
                    }
                }
            }
            catch (Exception ex)
            {
                return HandleException(ex);
            }
        }


        /// <summary>
        /// Compressed <see cref="HttpContent"/> class.
        /// </summary>
        /// <remarks>
        /// Shamefully snitched from https://github.com/WebApiContrib/WebAPIContrib/blob/master/src/WebApiContrib/Content/CompressedContent.cs.
        /// </remarks>
        private class CompressedContent : HttpContent
        {
            private readonly string encodingType;
            private readonly HttpContent originalContent;


            public CompressedContent(HttpContent content, string encodingType)
            {
                if (content == null)
                    throw new ArgumentNullException("content");

                if (encodingType == null)
                    throw new ArgumentNullException("encodingType");

                this.originalContent = content;
                this.encodingType = encodingType.ToLowerInvariant();

                if (this.encodingType != "gzip" && this.encodingType != "deflate")
                {
                    throw new InvalidOperationException(
                        string.Format("Encoding '{0}' is not supported. Only supports gzip or deflate encoding.", this.encodingType));
                }

                foreach (var header in this.originalContent.Headers)
                    Headers.TryAddWithoutValidation(header.Key, header.Value);

                Headers.ContentEncoding.Add(encodingType);
            }


            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    this.originalContent.Dispose();

                base.Dispose(disposing);
            }


            protected override Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                Stream compressedStream = null;

                switch (this.encodingType)
                {
                    case "gzip":
                        compressedStream = new GZipStream(stream, CompressionMode.Compress, true);
                        break;
                    case "deflate":
                        compressedStream = new DeflateStream(stream, CompressionMode.Compress, true);
                        break;
                }

                return this.originalContent.CopyToAsync(compressedStream).ContinueWith(tsk =>
                {
                    if (compressedStream != null)
                        compressedStream.Dispose();
                });
            }


            protected override bool TryComputeLength(out long length)
            {
                length = -1;

                return false;
            }
        }
    }
}

#endif