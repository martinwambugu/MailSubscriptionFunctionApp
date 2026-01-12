using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Abstractions;
using Microsoft.Azure.WebJobs.Extensions.OpenApi.Core.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.OpenApi.Models;
using System;
using System.Collections.Generic;

namespace MailSubscriptionFunctionApp.Infrastructure
{
    /// <summary>  
    /// Custom OpenAPI configuration for Azure Functions Worker v2.  
    /// Implements IOpenApiConfigurationOptions for dynamic metadata.  
    /// </summary>  
    public class OpenApiConfig : IOpenApiConfigurationOptions
    {
        /// <summary>  
        /// Parameterless constructor required by OpenAPI extension for reflection-based instantiation.  
        /// </summary>  
        public OpenApiConfig()
        {
            // ✅ Use default values when instantiated via reflection  
            Info = new OpenApiInfo
            {
                Title = "Mail Journaling User Functions API",
                Version = "1.0.0",
                Description = "API documentation for Mail Journaling User Functions",
                Contact = new OpenApiContact
                {
                    Name = "Creodata Solutions Ltd",
                    Email = "support@creodata.com"
                }
            };

#if DEBUG
            Servers = new List<OpenApiServer>
            {
                new OpenApiServer { Url = "http://localhost:7125", Description = "Local Development" }
            };
            ForceHttp = true;
            ForceHttps = false;
#else
            Servers = new List<OpenApiServer>  
            {  
                new OpenApiServer { Url = "https://mailjournalinguserfunctionsapp.azurewebsites.net", Description = "Azure Production" }  
            };  
            ForceHttp = false;  
            ForceHttps = true;  
#endif

            // ✅ Register document filters  
            DocumentFilters = new List<IDocumentFilter>
            {
                new SecurityDocumentFilter()
            };
        }

        /// <summary>  
        /// Constructor with configuration injection (used when registered in DI container).  
        /// </summary>  
        /// <param name="configuration">Application configuration.</param>  
        public OpenApiConfig(IConfiguration configuration) : this()
        {
            if (configuration == null)
                return;

            // ✅ Override defaults with configuration values if available  
            var apiTitle = configuration["OpenApi:Title"];
            var apiVersion = configuration["OpenApi:Version"];
            var apiDescription = configuration["OpenApi:Description"];
            var contactName = configuration["OpenApi:ContactName"];
            var contactEmail = configuration["OpenApi:ContactEmail"];

            if (!string.IsNullOrWhiteSpace(apiTitle))
                Info.Title = apiTitle;

            if (!string.IsNullOrWhiteSpace(apiVersion))
                Info.Version = apiVersion;

            if (!string.IsNullOrWhiteSpace(apiDescription))
                Info.Description = apiDescription;

            if (!string.IsNullOrWhiteSpace(contactName))
                Info.Contact.Name = contactName;

            if (!string.IsNullOrWhiteSpace(contactEmail))
                Info.Contact.Email = contactEmail;

            // ✅ Override server URLs from configuration if provided  
            var localServerUrl = configuration["OpenApi:LocalServerUrl"];
            var productionServerUrl = configuration["OpenApi:ProductionServerUrl"];

#if DEBUG
            if (!string.IsNullOrWhiteSpace(localServerUrl))
            {
                Servers = new List<OpenApiServer>
                {
                    new OpenApiServer { Url = localServerUrl, Description = "Local Development" }
                };
            }
#else
            if (!string.IsNullOrWhiteSpace(productionServerUrl))  
            {  
                Servers = new List<OpenApiServer>  
                {  
                    new OpenApiServer { Url = productionServerUrl, Description = "Azure Production" }  
                };  
            }  
#endif
        }

        public OpenApiInfo Info { get; set; }

        public OpenApiVersionType OpenApiVersion { get; set; } = OpenApiVersionType.V3;

        public bool IncludeRequestingHostName { get; set; } = true;

        public List<OpenApiServer> Servers { get; set; }

        public bool ForceHttp { get; set; }

        public bool ForceHttps { get; set; }

        public List<IDocumentFilter> DocumentFilters { get; set; }

        /// <summary>  
        /// Post-process the generated OpenAPI document to inject security schemes.  
        /// This ensures the "Authorize" button appears in Swagger UI.  
        /// </summary>  
        public void PostProcess(OpenApiDocument document)
        {
            if (document == null)
                throw new ArgumentNullException(nameof(document));

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, OpenApiSecurityScheme>();

            // ✅ API Key Scheme  
            if (!document.Components.SecuritySchemes.ContainsKey("ApiKeyAuth"))
            {
                var apiKeyScheme = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.ApiKey,
                    Name = "x-api-key",
                    In = ParameterLocation.Header,
                    Description = "API Key authentication using the `x-api-key` header",
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "ApiKeyAuth"
                    }
                };
                document.Components.SecuritySchemes["ApiKeyAuth"] = apiKeyScheme;
            }

            // ✅ Bearer Scheme  
            if (!document.Components.SecuritySchemes.ContainsKey("BearerAuth"))
            {
                var bearerScheme = new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "JWT",
                    Description = "JWT Bearer authentication (Azure AD)",
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "BearerAuth"
                    }
                };
                document.Components.SecuritySchemes["BearerAuth"] = bearerScheme;
            }

            // ✅ Root-level security requirements  
            document.SecurityRequirements ??= new List<OpenApiSecurityRequirement>();

            var hasApiKeyReq = document.SecurityRequirements
                .Any(req => req.Keys.Any(k => k.Reference?.Id == "ApiKeyAuth"));

            if (!hasApiKeyReq)
            {
                document.SecurityRequirements.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "ApiKeyAuth" }
                    }] = new List<string>()
                });
            }

            var hasBearerReq = document.SecurityRequirements
                .Any(req => req.Keys.Any(k => k.Reference?.Id == "BearerAuth"));

            if (!hasBearerReq)
            {
                document.SecurityRequirements.Add(new OpenApiSecurityRequirement
                {
                    [new OpenApiSecurityScheme
                    {
                        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "BearerAuth" }
                    }] = new List<string>()
                });
            }
        }
    }
}