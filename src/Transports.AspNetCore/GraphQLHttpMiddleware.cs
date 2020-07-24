using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Threading.Tasks;
using GraphQL.DataLoader;
using GraphQL.Http;
using GraphQL.Server.Transports.AspNetCore.Common;
using GraphQL.Types;
using GraphQL.Validation;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace GraphQL.Server.Transports.AspNetCore
{
    public class GraphQLHttpMiddleware<TSchema> where TSchema : ISchema
    {
        private const string JsonContentType = "application/json";
        private const string GraphQLContentType = "application/graphql";

        private readonly RequestDelegate _next;
        private readonly GraphQLHttpOptions _options;
        private readonly IDocumentExecuter _executer;
        private readonly DataLoaderDocumentListener _dataLoaderDocumentListener;
        private readonly IDocumentWriter _writer;
        private readonly TSchema _schema;
        private ILogger<GraphQLHttpMiddleware<TSchema>> _logger;

        public GraphQLHttpMiddleware(
            RequestDelegate next,
            IOptions<GraphQLHttpOptions> options,
            ILogger<GraphQLHttpMiddleware<TSchema>> logger,
            IDocumentExecuter executer,
            DataLoaderDocumentListener dataLoaderDocumentListener,
            IDocumentWriter writer,
            TSchema schema)
        {
            _next = next;
            _options = options.Value;
            _executer = executer;
            _dataLoaderDocumentListener = dataLoaderDocumentListener;
            _writer = writer;
            _schema = schema;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            if (!IsGraphQLRequest(context))
            {
                await _next(context);
                return;
            }

            await ExecuteAsync(context, _schema);
        }

        private bool IsGraphQLRequest(HttpContext context)
        {
            return HttpMethods.IsPost(context.Request.Method) && context.Request.Path.StartsWithSegments(_options.Path);
        }

        private async Task ExecuteAsync(HttpContext context, ISchema schema)
        {
            // Handle requests as per recommendation at http://graphql.org/learn/serving-over-http/
            var httpRequest = context.Request;
            var gqlRequest = new GraphQLRequest();

            object userContext = null;
            var userContextBuilder = context.RequestServices.GetService<IUserContextBuilder>();
            if (userContextBuilder != null)
            {
                userContext = await userContextBuilder.BuildUserContext(context);
            }
            else
            {
                userContext = _options.BuildUserContext?.Invoke(context);
            }

            if (userContext == null)
            {
                context.Response.ContentType = "application/json";
                context.Response.StatusCode = (int) HttpStatusCode.Unauthorized;
                
                return;
            }

            if (HttpMethods.IsGet(httpRequest.Method) || (HttpMethods.IsPost(httpRequest.Method) && httpRequest.Query.ContainsKey(GraphQLRequest.QueryKey)))
            {
                ExtractGraphQLRequestFromQueryString(httpRequest.Query, gqlRequest);
            }
            else if (HttpMethods.IsPost(httpRequest.Method))
            {
                if (!MediaTypeHeaderValue.TryParse(httpRequest.ContentType, out MediaTypeHeaderValue mediaTypeHeader))
                {
                    await WriteResponseAsync(context, HttpStatusCode.BadRequest, $"Invalid 'Content-Type' header: value '{httpRequest.ContentType}' could not be parsed.");
                    return;
                }

                switch (mediaTypeHeader.MediaType)
                {
                    case JsonContentType:
                        var body = Deserialize<dynamic>(httpRequest.Body);
                        if (body.Type == JTokenType.Array)
                        {
                            var array = (body as JArray).ToArray<dynamic>();

                            var results = await Task<ExecutionResult>.WhenAll(
                                array.Select<dynamic, Task<ExecutionResult>>(r =>
                                {
                                    var query = r.query.ToString();
                                    var operation = r.operationName.ToString();
                                    var variables = (JObject) r.variables;

                                    return ExecuteGraphQlRequest(schema, query, operation, variables, userContext);
                                }));

                            await WriteResponseAsync(context, results);
                        }
                        else
                        {
                            var jobject = body as JObject;

                            gqlRequest = jobject.ToObject<GraphQLRequest>();
                            await ProcessGraphQlRequest(context, schema, gqlRequest.Query, gqlRequest.OperationName, gqlRequest.Variables, userContext);
                        }

                        break;
                    case GraphQLContentType:
                        gqlRequest.Query = await ReadAsStringAsync(httpRequest.Body);
                        await ProcessGraphQlRequest(context, schema, gqlRequest.Query, gqlRequest.OperationName, gqlRequest.Variables, userContext);
                        break;
                    default:
                        await WriteResponseAsync(context, HttpStatusCode.BadRequest,
                            $"Invalid 'Content-Type' header: non-supported media type. Must be of '{JsonContentType}' or '{GraphQLContentType}'. See: http://graphql.org/learn/serving-over-http/.");
                        return;
                }
            }
        }

        private async Task ProcessGraphQlRequest(HttpContext context, ISchema schema, string query, string operationName, JObject variables, object userContext)
        {
            try
            {
                var result = await ExecuteGraphQlRequest(schema, query, operationName, variables, userContext);

                await WriteResponseAsync(context, result);
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
                throw e;
            }
        }

        private Task<ExecutionResult> ExecuteGraphQlRequest(ISchema schema, string query, string operationName, JObject variables, object userContext)
        {
#if DEBUG
            if (!string.IsNullOrEmpty(operationName))
            {
                _logger.LogDebug($"ExecuteGraphQlRequest({operationName})");                
            }
            
            _logger.LogDebug($"Query:  {query.Truncate(150).Replace("\n", " ")}");
            if (variables != null && variables.Count > 0) 
                _logger.LogDebug($"\tInputs:  {variables.ToString(Formatting.None).Replace("\n", " ").Truncate(100)}");
            
#endif

            return _executer.ExecuteAsync(_ =>
            {
                _.Schema = schema;
                _.Query = query;
                _.OperationName = operationName;
                _.Inputs = variables.ToInputs();
                _.UserContext = userContext;
                _.ComplexityConfiguration = _options.ComplexityConfiguration;
                _.EnableMetrics = _options.EnableMetrics;
                _.ExposeExceptions = _options.ExposeExceptions;
                _.SetFieldMiddleware = _options.SetFieldMiddleware;
                _.ValidationRules = _options.ValidationRules.Concat(DocumentValidator.CoreRules()).ToList();
                _.Listeners.Add(_dataLoaderDocumentListener);
            });
        }


        private async Task WriteResponseAsync(HttpContext context, HttpStatusCode statusCode, string errorMessage)
        {
            var result = new ExecutionResult()
            {
                Errors = new ExecutionErrors()
            };
            _logger.LogError($"Error responding to GraphQL request:  {errorMessage}");
            result.Errors.Add(new ExecutionError(errorMessage));


            await WriteResponseAsync(context, result);
        }

        private async Task WriteResponseAsync(HttpContext context, ExecutionResult[] results)
        {
            var json = _writer.Write(results);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int) HttpStatusCode.OK;

            if (results.Any(r => r.Errors?.Any() == true))
            {
                context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
                _logger.LogError($"Error responding to GraphQL request:  ");
                foreach (var r in results)
                {
                    if (r != null && r.Errors != null)
                    {
                        foreach (var e in r.Errors)
                        {
                            _logger.LogError(e.InnerException != null ? e.InnerException.ToString() : e.ToString());
                        }
                    }
                }
            }

            await context.Response.WriteAsync(json);
        }

        private async Task WriteResponseAsync(HttpContext context, ExecutionResult result)
        {
            var json = _writer.Write(result);

            context.Response.ContentType = "application/json";
            context.Response.StatusCode = (int) HttpStatusCode.OK;

            if (result.Errors?.Any() == true)
            {
                context.Response.StatusCode = (int) HttpStatusCode.BadRequest;
                _logger.LogError($"Error responding to GraphQL request:  ");
                foreach (var e in result.Errors)
                {
                    _logger.LogError(e.InnerException != null ? e.InnerException.ToString() : e.ToString());
                }
            }

            await context.Response.WriteAsync(json);
        }

        private static T Deserialize<T>(Stream s)
        {
            using (var reader = new StreamReader(s))
            using (var jsonReader = new JsonTextReader(reader))
            {
                return new JsonSerializer().Deserialize<T>(jsonReader);
            }
        }

        private static async Task<string> ReadAsStringAsync(Stream s)
        {
            using (var reader = new StreamReader(s))
            {
                return await reader.ReadToEndAsync();
            }
        }

        private static void ExtractGraphQLRequestFromQueryString(IQueryCollection qs, GraphQLRequest gqlRequest)
        {
            gqlRequest.Query = qs.TryGetValue(GraphQLRequest.QueryKey, out StringValues queryValues) ? queryValues[0] : null;
            gqlRequest.Variables = qs.TryGetValue(GraphQLRequest.VariablesKey, out StringValues variablesValues) ? JObject.Parse(variablesValues[0]) : null;
            gqlRequest.OperationName = qs.TryGetValue(GraphQLRequest.OperationNameKey, out StringValues operationNameValues) ? operationNameValues[0] : null;
        }
    }
}