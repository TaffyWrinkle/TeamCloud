﻿/**
 *  Copyright (c) Microsoft Corporation.
 *  Licensed under the MIT License.
 */

using System;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flurl.Http.Configuration;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Microsoft.Azure.Management.ResourceManager.Fluent.Core;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using AZFluent = Microsoft.Azure.Management.Fluent;
using IHttpClientFactory = Flurl.Http.Configuration.IHttpClientFactory;
using RMFluent = Microsoft.Azure.Management.ResourceManager.Fluent;

namespace TeamCloud.Azure
{

    public interface IAzureSessionService
    {
        AzureEnvironment Environment { get; }

        IAzureSessionOptions Options { get; }

        AZFluent.Azure.IAuthenticated CreateSession();

        AZFluent.IAzure CreateSession(Guid subscriptionId);

        Task<string> AcquireTokenAsync(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint);

        Task<IAzureSessionIdentity> GetIdentityAsync(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint);

        RestClient CreateClient(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint);

        T CreateClient<T>(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint, Guid? subscriptionId = null) where T : FluentServiceClientBase<T>;
    }

    public class AzureSessionService : IAzureSessionService
    {
        public static bool IsAzureEnvironment =>
            !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WEBSITE_INSTANCE_ID"));

        public static Task<string> AcquireTokenAsync(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint, IAzureSessionOptions azureSessionOptions = null, IHttpClientFactory httpClientFactory = null)
            => new AzureSessionService(azureSessionOptions, httpClientFactory).AcquireTokenAsync(azureEndpoint);

        private readonly Lazy<AzureCredentials> credentials;
        private readonly Lazy<AZFluent.Azure.IAuthenticated> session;
        private readonly IAzureSessionOptions azureSessionOptions;
        private readonly IHttpClientFactory httpClientFactory;

        public AzureSessionService(IAzureSessionOptions azureSessionOptions = null, IHttpClientFactory httpClientFactory = null)
        {
            this.azureSessionOptions = azureSessionOptions ?? AzureSessionOptions.Default;
            this.httpClientFactory = httpClientFactory ?? new DefaultHttpClientFactory();

            credentials = new Lazy<AzureCredentials>(() => InitCredentials(), LazyThreadSafetyMode.PublicationOnly);
            session = new Lazy<AZFluent.Azure.IAuthenticated>(() => InitSession(), LazyThreadSafetyMode.PublicationOnly);
        }

        private AzureCredentials InitCredentials()
        {
            try
            {
                if (string.IsNullOrEmpty(azureSessionOptions.TenantId) && azureSessionOptions == AzureSessionOptions.Default)
                {
                    var tenantId = GetIdentityAsync(AzureEndpoint.ResourceManagerEndpoint).Result?.TenantId;

                    if (tenantId.HasValue)
                        ((AzureSessionOptions)azureSessionOptions).TenantId = tenantId.ToString();
                }

                var credentialsFactory = new RMFluent.Authentication.AzureCredentialsFactory();

                if (string.IsNullOrEmpty(azureSessionOptions.ClientId))
                {
                    if (IsAzureEnvironment)
                    {
                        return credentialsFactory
                            .FromSystemAssignedManagedServiceIdentity(MSIResourceType.AppService, Environment, azureSessionOptions.TenantId);
                    }
                    else
                    {
                        return new AzureCredentials(
                            new TokenCredentials(new DevelopmentTokenProvider(this, AzureEndpoint.ResourceManagerEndpoint)),
                            new TokenCredentials(new DevelopmentTokenProvider(this, AzureEndpoint.GraphEndpoint)),
                            azureSessionOptions.TenantId,
                            Environment);
                    }
                }
                else if (string.IsNullOrEmpty(azureSessionOptions.ClientSecret))
                {
                    return credentialsFactory
                        .FromUserAssigedManagedServiceIdentity(azureSessionOptions.ClientId, MSIResourceType.AppService, this.Environment, azureSessionOptions.TenantId);
                }
                else
                {
                    return credentialsFactory
                        .FromServicePrincipal(azureSessionOptions.ClientId, azureSessionOptions.ClientSecret, azureSessionOptions.TenantId, this.Environment);
                }
            }
            catch (Exception exc)
            {
                throw new TypeInitializationException(typeof(AzureCredentials).FullName, exc);
            }
        }

        private AZFluent.Azure.IAuthenticated InitSession()
        {
            return AZFluent.Azure
                .Configure()
                .WithDelegatingHandler(this.httpClientFactory)
                .Authenticate(credentials.Value);
        }

        public AzureEnvironment Environment { get => AzureEnvironment.AzureGlobalCloud; }

        public IAzureSessionOptions Options { get => azureSessionOptions; }

        public async Task<IAzureSessionIdentity> GetIdentityAsync(AzureEndpoint azureEndpoint)
        {
            var token = await AcquireTokenAsync(azureEndpoint)
                .ConfigureAwait(false);

            var jwtToken = new JwtSecurityTokenHandler()
                .ReadJwtToken(token);

            var identity = new AzureSessionIdentity();

            if (jwtToken.Payload.TryGetValue("tid", out var tidValue) && Guid.TryParse(tidValue.ToString(), out Guid tid))
            {
                identity.TenantId = tid;
            }

            if (jwtToken.Payload.TryGetValue("oid", out var oidValue) && Guid.TryParse(oidValue.ToString(), out Guid oid))
            {
                identity.ObjectId = oid;
            }

            if (jwtToken.Payload.TryGetValue("appid", out var appidValue) && Guid.TryParse(appidValue.ToString(), out Guid appid))
            {
                identity.ClientId = appid;
            }

            return identity;
        }

        public async Task<string> AcquireTokenAsync(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint)
        {
            if (string.IsNullOrEmpty(azureSessionOptions.ClientId))
            {
                AzureServiceTokenProvider tokenProvider;

                if (IsAzureEnvironment)
                {
                    tokenProvider = new AzureServiceTokenProvider("RunAs=App");
                }
                else
                {
                    // ensure we disable SSL verfication for this process when using the Azure CLI to aqcuire MSI token.
                    // otherwise our code will fail in dev scenarios where a dev proxy like fiddler is running to sniff
                    // http traffix between our services or between service and other reset apis (e.g. Azure)
                    System.Environment.SetEnvironmentVariable("AZURE_CLI_DISABLE_CONNECTION_VERIFICATION", "1", EnvironmentVariableTarget.Process);

                    tokenProvider = new AzureServiceTokenProvider("RunAs=Developer;DeveloperTool=AzureCLI");
                }

                return await tokenProvider
                    .GetAccessTokenAsync(this.Environment.GetEndpointUrl(azureEndpoint))
                    .ConfigureAwait(false);
            }
            else if (string.IsNullOrEmpty(azureSessionOptions.ClientSecret))
            {
                var tokenProvider = new AzureServiceTokenProvider($"RunAs=App;AppId={azureSessionOptions.ClientId}");

                return await tokenProvider
                    .GetAccessTokenAsync(this.Environment.GetEndpointUrl(azureEndpoint))
                    .ConfigureAwait(false);
            }
            else
            {
                var authenticationContext = new AuthenticationContext($"{this.Environment.AuthenticationEndpoint}{azureSessionOptions.TenantId}/", true);

                var authenticationResult = await authenticationContext
                    .AcquireTokenAsync(this.Environment.GetEndpointUrl(azureEndpoint), new ClientCredential(azureSessionOptions.ClientId, azureSessionOptions.ClientSecret))
                    .ConfigureAwait(false);

                return authenticationResult.AccessToken;
            }
        }

        public AZFluent.Azure.IAuthenticated CreateSession()
            => session.Value;

        public AZFluent.IAzure CreateSession(Guid subscriptionId)
            => CreateSession().WithSubscription(subscriptionId.ToString());

        public RestClient CreateClient(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint)
        {
            try
            {
                var endpointUrl = AzureEnvironment.AzureGlobalCloud.GetEndpointUrl(azureEndpoint);

                return RestClient.Configure()
                    .WithBaseUri(endpointUrl)
                    .WithCredentials(credentials.Value)
                    .WithDelegatingHandler(httpClientFactory)
                    .Build();
            }
            catch (TypeInitializationException)
            {
                throw;
            }
            catch (Exception exc)
            {
                throw new TypeInitializationException(typeof(RestClient).FullName, exc);
            }
        }

        public T CreateClient<T>(AzureEndpoint azureEndpoint = AzureEndpoint.ResourceManagerEndpoint, Guid? subscriptionId = null)
            where T : FluentServiceClientBase<T>
        {
            try
            {
                var client = (T)Activator.CreateInstance(typeof(T), new object[]
                {
                    CreateClient(azureEndpoint)
                });

                // if a subscription id was provided by the caller
                // set the corresponding property on the client instance

                if (subscriptionId.HasValue
                    && typeof(T).TryGetProperty("SubscriptionId", out PropertyInfo subscriptionPropertyInfo)
                    && subscriptionPropertyInfo.PropertyType == typeof(string))
                    subscriptionPropertyInfo.SetValue(client, subscriptionId.Value.ToString());

                // check if the client instance has a tenant id property
                // which is not yet initialized - if so, use the tenant id
                // provided by the session options and initialize the client

                if (typeof(T).TryGetProperty("TenantID", out PropertyInfo tenantPropertyInfo)
                    && tenantPropertyInfo.PropertyType == typeof(string)
                    && string.IsNullOrEmpty(tenantPropertyInfo.GetValue(client) as string))
                    tenantPropertyInfo.SetValue(client, azureSessionOptions.TenantId);

                return client;
            }
            catch (TypeInitializationException)
            {
                throw;
            }
            catch (Exception exc)
            {
                throw new TypeInitializationException(typeof(T).FullName, exc);
            }
        }

        private class DevelopmentTokenProvider : ITokenProvider
        {
            private readonly IAzureSessionService azureSessionService;
            private readonly AzureEndpoint azureEndpoint;

            public DevelopmentTokenProvider(IAzureSessionService azureSessionService, AzureEndpoint azureEndpoint)
            {
                this.azureSessionService = azureSessionService ?? throw new ArgumentNullException(nameof(azureSessionService));
                this.azureEndpoint = azureEndpoint;
            }

            public async Task<AuthenticationHeaderValue> GetAuthenticationHeaderAsync(CancellationToken cancellationToken)
            {
                var token = await azureSessionService.AcquireTokenAsync(azureEndpoint).ConfigureAwait(false);

                return new AuthenticationHeaderValue("Bearer", token);
            }
        }
    }
}
