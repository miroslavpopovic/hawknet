﻿using Microsoft.ServiceModel.Web;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IdentityModel.Claims;
using System.IdentityModel.Policy;
using System.Linq;
using System.Net;
using System.Security;
using System.Security.Principal;
using System.ServiceModel;
using System.ServiceModel.Channels;
using System.ServiceModel.Security;
using System.ServiceModel.Web;
using System.Text;
using System.Web;

namespace HawkNet.WCF
{
    public class HawkRequestInterceptor : RequestInterceptor
    {
        const string HawkScheme = "Hawk";

        static TraceSource TraceSource = new TraceSource("HawkNet.WCF");

        Func<string, HawkCredential> credentials;
        bool sendChallenge;
        Predicate<Message> endpointFilter;
        int timeskewInSeconds = 60;

        public HawkRequestInterceptor(Func<string, HawkCredential> credentials, bool sendChallenge = true,
            Predicate<Message> endpointFilter = null,
            int timeskewInSeconds = 60)
            : base(false)
        {
            this.credentials = credentials;
            this.sendChallenge = sendChallenge;
            this.endpointFilter = endpointFilter;
            this.timeskewInSeconds = timeskewInSeconds;
        }

        public override void ProcessRequest(ref System.ServiceModel.Channels.RequestContext requestContext)
        {
            if (Trace.CorrelationManager.ActivityId == Guid.Empty)
                Trace.CorrelationManager.ActivityId = Guid.NewGuid();

            var request = requestContext.RequestMessage;
            
            if (endpointFilter == null || endpointFilter(request))
            {
                try
                {
                    IPrincipal principal = ExtractCredentials(request);
                    if (principal != null)
                    {
                        InitializeSecurityContext(request, principal);
                    }
                    else
                    {
                        var reply = Message.CreateMessage(MessageVersion.None, null);
                        var responseProperty = new HttpResponseMessageProperty() { StatusCode = HttpStatusCode.Unauthorized };

                        if (sendChallenge)
                        {
                            var ts = Hawk.ConvertToUnixTimestamp(DateTime.Now).ToString();
                            var challenge = string.Format("ts=\"{0}\" ntp=\"{1}\"",
                                ts, "pool.ntp.org");

                            responseProperty.Headers.Add("WWW-Authenticate", challenge);
                        }

                        reply.Properties[HttpResponseMessageProperty.Name] = responseProperty;
                        requestContext.Reply(reply);

                        requestContext = null;
                    }
                }
                catch (SecurityException ex)
                {
                    TraceSource.TraceData(TraceEventType.Error, 0,
                        string.Format("{0} - Security Exception {1}",
                            Trace.CorrelationManager.ActivityId, ex.ToString()));

                    var reply = Message.CreateMessage(MessageVersion.None, null, (object)ex.Message);
                    var responseProperty = new HttpResponseMessageProperty() { StatusCode = HttpStatusCode.Unauthorized };

                    reply.Properties[HttpResponseMessageProperty.Name] = responseProperty;
                    requestContext.Reply(reply);

                    requestContext = null;
                }
            }
        }

        private IPrincipal ExtractCredentials(Message requestMessage)
        {
            var request = (HttpRequestMessageProperty)requestMessage.Properties[HttpRequestMessageProperty.Name];
            
            var authHeader = request.Headers["Authorization"];

            if (authHeader != null && authHeader.StartsWith(HawkScheme, StringComparison.InvariantCultureIgnoreCase))
            {
                var hawk = authHeader.Substring(HawkScheme.Length).Trim();

                TraceSource.TraceInformation(string.Format("{0} - Received Auth header: {1}",
                    Trace.CorrelationManager.ActivityId, hawk));

                var decodedUrl = HttpUtility.UrlDecode(requestMessage.Properties.Via.AbsoluteUri);

                var principal = Hawk.Authenticate(hawk,
                    request.Headers["host"],
                    request.Method,
                    new Uri(decodedUrl),
                    this.credentials,
                    this.timeskewInSeconds);

                return principal;
                
            }

            return null;
        }

        private void InitializeSecurityContext(Message request, IPrincipal principal)
        {
            var policies = new List<IAuthorizationPolicy>();
            policies.Add(new PrincipalAuthorizationPolicy(principal));
            
            var securityContext = new ServiceSecurityContext(policies.AsReadOnly());

            if (request.Properties.Security != null)
            {
                request.Properties.Security.ServiceSecurityContext = securityContext;
            }
            else
            {
                request.Properties.Security = new SecurityMessageProperty() { ServiceSecurityContext = securityContext };
            }
        }

        class PrincipalAuthorizationPolicy : IAuthorizationPolicy
        {
            string id = Guid.NewGuid().ToString();
            IPrincipal user;

            public PrincipalAuthorizationPolicy(IPrincipal user)
            {
                this.user = user;
            }

            public ClaimSet Issuer
            {
                get { return ClaimSet.System; }
            }

            public string Id
            {
                get { return this.id; }
            }

            public bool Evaluate(EvaluationContext evaluationContext, ref object state)
            {
                evaluationContext.AddClaimSet(this, new DefaultClaimSet(Claim.CreateNameClaim(user.Identity.Name)));
                evaluationContext.Properties["Identities"] = new List<IIdentity>(new IIdentity[] { user.Identity });
                evaluationContext.Properties["Principal"] = user;
                return true;
            }
        }
    }


}
