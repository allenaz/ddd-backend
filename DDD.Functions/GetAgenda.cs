using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using DDD.Functions.Config;
using DDD.Core.DocumentDb;
using DDD.Sessionize;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Net;
using Newtonsoft.Json.Serialization;
using Newtonsoft.Json;

namespace DDD.Functions
{
    public static class GetAgenda
    {
        private static readonly Random Random = new Random();

        [FunctionName("GetAgenda")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = null)]
            HttpRequest req,
            ILogger log,
            [BindSubmissionsAndVotingConfig]
            SubmissionsAndVotingConfig config)
        {
            if (config.Now < config.SubmissionsAvailableToDate)
            {
                log.LogWarning("Attempt to access GetAgenda endpoint before they are available at {availableDate}.", config.SubmissionsAvailableToDate);
                return new StatusCodeResult(404);
            }

            var documentDbClient = DocumentDbAccount.Parse(config.SessionsConnectionString);
            var repo = new DocumentDbRepository<SessionOrPresenter>(documentDbClient, config.CosmosDatabaseId, config.CosmosCollectionId);
            await repo.InitializeAsync();
            var all = await repo.GetAllItemsAsync();

            var presenters = all.Where(x => x.Presenter != null).Select(x => x.Presenter).ToArray();
            var agenda = all.Where(x => x.Session != null && x.Session.InAgenda)
                .Select(x => x.Session)
                .Select(s => new Session
                {
                    Id = s.Id.ToString(),
                    Title = s.Title, 
                    Abstract = s.Abstract,
                    Format = s.Format,
                    Level = s.Level,
                    Tags = s.Tags,
                    Presenters = s.PresenterIds.Select(pId => presenters.Where(p => p.Id == pId).Select(p => new Presenter
                    {
                        Id = p.Id.ToString(),
                        Name = p.Name,
                        Tagline = p.Tagline,
                        Bio = p.Bio,
                        ProfilePhotoUrl = p.ProfilePhotoUrl,
                        TwitterHandle = p.TwitterHandle,
                        WebsiteUrl = p.WebsiteUrl
                    }).Single()).ToArray()
                })
                .OrderBy(x => Random.Next())
                .ToArray();

            var settings = new JsonSerializerSettings();
            settings.ContractResolver = new DefaultContractResolver();

            return new JsonResult(agenda, settings);
        }

        public class Session
        {
            public string Id { get; set; }
            public string Title { get; set; }
            public string Abstract { get; set; }
            public string Format { get; set; }
            public string Level { get; set; }
            public string[] Tags { get; set; }
            public Presenter[] Presenters { get; set; }
        }

        public class Presenter
        {
            public string Id { get; set; }
            public string Name { get; set; }
            public string Tagline { get; set; }
            public string Bio { get; set; }
            public string ProfilePhotoUrl { get; set; }
            public string TwitterHandle { get; set; }
            public string WebsiteUrl { get; set; }
        }
    }
}
