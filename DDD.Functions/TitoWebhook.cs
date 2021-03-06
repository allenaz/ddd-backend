using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using DDD.Core.AzureStorage;
using DDD.Functions.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace DDD.Functions
{
    public static class TitoWebhook
    {
        [FunctionName("TitoWebhook")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]
            HttpRequestMessage req,
            ILogger log,
            [BindTitoWebhookConfig]
            TitoWebhookConfig config)
        {
            if (string.IsNullOrEmpty(config.Secret))
            {
                log.LogWarning("Received Tito webhook request, but webhook isn't configured.");
                return new StatusCodeResult(404);
            }

            // Verify signature to ensure request came from Tito
            var signature = req.Headers.Where(h => h.Key == "Tito-Signature").SelectMany(x => x.Value).FirstOrDefault();
            var payload = await req.Content.ReadAsStringAsync();
            var expectedSignature = Convert.ToBase64String(
                new HMACSHA256(Encoding.UTF8.GetBytes(config.Secret))
                    .ComputeHash(Encoding.UTF8.GetBytes(payload)));
            if (signature != expectedSignature)
            {
                log.LogWarning("Received invalid payload signature: {payload}, {signature}, {expectedSignature}", payload, signature, expectedSignature);
                return new StatusCodeResult(400);
            }
            log.LogDebug("Received valid signature {signature}", signature);

            var eventType = req.Headers.Where(h => h.Key == "X-Webhook-Name").SelectMany(x => x.Value).FirstOrDefault();

            // Prevent duplicate webhook handling
            var webhookPayload = JsonConvert.DeserializeObject<WebhookPayload>(payload);
            var (deDupeRepo, orderNotificationQueue, ticketNotificationQueue) = await config.GetRepositoryAsync();
            var processedWebhook = new DedupeWebhookEntity("Tito", eventType, webhookPayload.Id);
            var existingProcessing = await deDupeRepo.GetAsync(processedWebhook.PartitionKey, processedWebhook.RowKey);
            if (existingProcessing != null)
            {
                log.LogInformation("Received duplicate webhook with id {id}", webhookPayload.Id);
                return new StatusCodeResult(202);
            }

            // Tickets purchased and name/email added to registration
            if (eventType == "registration.finished")
            {
                var registration = JsonConvert.DeserializeObject<RegistrationPayload>(payload);
                var notification = new OrderNotificationEvent
                {
                    OrdererName = registration.OrdererName,
                    EventName = registration.Event.Name,
                    OrderNumber = registration.OrderNumber,
                    AdminUrl = registration.OrderAdminUrl,
                    Total = registration.Total,
                    TicketsPurchasedDescription = string.Join(",", registration.LineItems.Select(l => $"{l.Quantity} x {l.TicketType} @ {l.Total:C}"))
                };
                log.LogInformation("Pushing order notification to queue for order {orderNumber}", registration.OrderNumber);
                await orderNotificationQueue.PushAsync(notification);
            }

            // Ticket completed, including attendee information / Q&A
            if (eventType == "ticket.completed")
            {
                var ticket = JsonConvert.DeserializeObject<TicketPayload>(payload);
                var notification = new TicketNotificationEvent
                {
                    AttendeeName = ticket.AttendeeName,
                    EventName = ticket.Event.Name,
                    TicketClass = ticket.TicketClass,
                    TicketNumber = ticket.TicketNumber,
                    AdminUrl = ticket.TicketAdminUrl
                };
                log.LogInformation("Pushing ticket notification to queue for ticket {ticketNumber}", ticket.TicketNumber);
                await ticketNotificationQueue.PushAsync(notification);
            }

            await deDupeRepo.CreateAsync(processedWebhook);

            return new StatusCodeResult(202);
        }
    }

    public class WebhookPayload
    {
        [JsonProperty("slug")]
        public string Id { get; set; }
    }

    public class TicketPayload
    {
        [JsonProperty("event")]
        public Event Event { get; set; }

        [JsonProperty("name")]
        public string AttendeeName { get; set; }

        [JsonProperty("first_name")]
        public string AttendeeFirstName { get; set; }

        [JsonProperty("last_name")]
        public string AttendeeLastName { get; set; }

        [JsonProperty("email")]
        public string AttendeeEmail { get; set; }

        [JsonProperty("slug")]
        public string TicketId { get; set; }

        [JsonProperty("reference")]
        public string TicketNumber { get; set; }

        [JsonProperty("release_title")]
        public string TicketClass { get; set; }

        [JsonProperty("state_name")]
        public string TicketState { get; set; }

        [JsonProperty("admin_url")]
        public string TicketAdminUrl { get; set; }

        [JsonProperty("release_slug")]
        public string LineItemSlug { get; set; }

        [JsonProperty("responses")]
        public Dictionary<string, string> Responses { get; set; }

        [JsonProperty("custom")]
        public string CustomData { get; set; }
    }

    public class Event
    {
        public string Id => $"{Account}/{Slug}";

        [JsonProperty("title")]
        public string Name { get; set; }

        [JsonProperty("account_slug")]
        public string Account { get; set; }

        [JsonProperty("slug")]
        public string Slug { get; set; }
    }

    public class RegistrationPayload
    {
        [JsonProperty("event")]
        public Event Event { get; set; }

        [JsonProperty("name")]
        public string OrdererName { get; set; }

        [JsonProperty("first_name")]
        public string OrdererFirstName { get; set; }

        [JsonProperty("last_name")]
        public string OrdererLastName { get; set; }

        [JsonProperty("email")]
        public string OrdererEmail { get; set; }

        [JsonProperty("slug")]
        public string OrderId { get; set; }

        [JsonProperty("reference")]
        public string OrderNumber { get; set; }

        [JsonProperty("paid")]
        public bool OrderPaid { get; set; }

        [JsonProperty("admin_url")]
        public string OrderAdminUrl => $"https://ti.to/{Event.Id}/admin/registrations/{OrderId}";

        [JsonProperty("line_items")]
        public IEnumerable<LineItemPayload> LineItems { get; set; }

        [JsonProperty("custom")]
        public string CustomData { get; set; }

        [JsonProperty("total")]
        public decimal Total { get; set; }
}

    public class LineItemPayload
    {
        [JsonProperty("release_slug")]
        public string Slug { get; set; }

        [JsonProperty("title")]
        public string TicketType { get; set; }

        [JsonProperty("quantity")]
        public int Quantity { get; set; }

        [JsonProperty("total")]
        public decimal Total { get; set; }
    }

}

/*
 * https://ti.to/docs/webhook
 * POST request with the following headers:
 *
 * X-Webhook-Name: ticket.created
 * X-Webhook-Endpoint-Id: 8783
 * Tito-Signature: MLJycdoMuK8HhcDXXXXXXXECqzTglMzvXd/a/Uco/fY=
 *
 * X-Webhook-Name indicates the event type:
 *  * registration.started (First select qty and click Continue)
 *    {
 *      "created_at": "2019-04-23T15:30:43.953Z",
 *      "text": " <> has started to register",
 *      "url": "https://ti.to/dddperth/committee-meeting/registrations/pbTMJEedGbEivL6F5D61Yzg",
 *      "slug": "pbTMJEedGbEivL6F5D61Yzg",
 *      "custom": "",
 *      "line_items": [
 *        {
 *          "release_slug": "z-zzobiair8",
 *          "price": "0.00",
 *          "title": "General Attendee",
 *          "quantity": 1,
 *          "total": "0.00",
 *          "currency": "AUD"
 *        }
 *      ]
 *    }
 *  * registration.filling (start entering details - name and email, fires multiple times?)
 *  * registration.finished (when finished filling your details - name and email)
 *    {
 *      "id": 4997470,
 *      "text": "Rob Moore \u003crob.moore@readify.net\u003e registered 1 ticket https://ti.to/dddperth/committee-meeting/admin/registrations/pbTMJEedGbEivL6F5D61Yzg",
 *      "event": {
 *        "id": 1079307,
 *        "title": "committee meeting",
 *        "url": "https://ti.to/dddperth/committee-meeting",
 *        "account_slug": "dddperth",
 *        "slug": "committee-meeting",
 *        "start_date": "2019-08-03",
 *        "end_date": "2019-08-03",
 *        "metadata": null
 *      },
 *      "slug": "pbTMJEedGbEivL6F5D61Yzg",
 *      "reference": "GOBX",
 *      "currency": "AUD",
 *      "total": "0.00",
 *      "total_less_tax": "0.00",
 *      "name": "Rob Moore",
 *      "first_name": "Rob",
 *      "last_name": "Moore",
 *      "email": "rob.moore@xxx.net",
 *      "phone_number": "",
 *      "company_name": null,
 *      "discount_code": null,
 *      "payment_reference": null,
 *      "created_at": "2019-04-23T15:30:43.000Z",
 *      "created_date": "2019-04-23",
 *      "completed_at": "2019-04-23T15:33:11.874Z",
 *      "completed_date": "2019-04-23",
 *      "custom": "",
 *      "metadata": null,
 *      "updated_at": "2019-04-23T15:33:11.963Z",
 *      "paid": true,
 *      "line_items": [
 *        {
 *          "id": 5425392,
 *          "release_slug": "z-zzobiair8",
 *          "release_id": 1178066,
 *          "release_title": "General Attendee",
 *          "release_price": "0.00",
 *          "release": {
 *            "slug": "z-zzobiair8",
 *            "title": "General Attendee",
 *            "price": "0.00",
 *            "metadata": null
 *          },
 *          "price": "0.00",
 *          "title": "General Attendee",
 *          "quantity": 1,
 *          "total": "0.00",
 *          "currency": "AUD"
 *        }
 *      ],
 *      "quantities": {
 *        "z-zzobiair8": {
 *          "release": "General Attendee",
 *          "quantity": 1
 *        }
 *      },
 *      "tickets": [
 *        {
 *          "reference": "GOBX-1",
 *          "slug": "ppaxzZ0Ck1aRuvwUOZihJfg",
 *          "price": "0.00",
 *          "price_less_tax": "0.00",
 *          "total_paid": "0.00",
 *          "total_paid_less_tax": "0.00",
 *          "release_id": 1178066,
 *          "release_slug": "z-zzobiair8",
 *          "release_title": "General Attendee",
 *          "release": {
 *            "id": 1178066,
 *            "slug": "z-zzobiair8",
 *            "title": "General Attendee",
 *            "price": "0.00",
 *            "metadata": null
 *          },
 *          "name": "",
 *          "first_name": null,
 *          "last_name": null,
 *          "company_name": null,
 *          "email": null,
 *          "url": "https://ti.to/tickets/ppaxzZ0Ck1aRuvwUOZihJfg",
 *          "admin_url": "https://ti.to/dddperth/committee-meeting/admin/tickets/ppaxzZ0Ck1aRuvwUOZihJfg",
 *          "responses": null,
 *          "answers": [
 *            
 *          ]
 *        }
 *      ],
 *      "receipt": {
 *        "number": "0000011",
 *        "total": "0.00",
 *        "tax": 0,
 *        "total_less_tax": "0.00",
 *        "payment_provider": "",
 *        "payment_reference": null,
 *        "paid": true
 *      }
 *    }   
 *  * ticket.created (per ticket, after registration is filled)
 *  * ticket.updated (per ticket, when entering details against a ticket and saving)
 *  * ticket.completed (per ticket, when entering required info, also get a ticket.updated)
 *    {
 *      "text": "Childcare PWBH-2 completed",
 *      "id": 4398537,
 *      "event": {
 *        "id": 1079307,
 *        "title": "committee meeting",
 *        "url": "https://ti.to/dddperth/committee-meeting",
 *        "account_slug": "dddperth",
 *        "slug": "committee-meeting",
 *        "currency": "AUD",
 *        "start_date": "2019-08-03",
 *        "end_date": "2019-08-03"
 *      },
 *      "name": "My Child",
 *      "first_name": "My",
 *      "last_name": "Child",
 *      "email": "rob.moore@xxx.net",
 *      "phone_number": null,
 *      "company_name": "",
 *      "reference": "PWBH-2",
 *      "price": "0.00",
 *      "tax": "0.00",
 *      "price_less_tax": "0.00",
 *      "slug": "p0oR6QJehlzYdduZw5GMq4w",
 *      "state_name": "complete",
 *      "gender": "female",
 *      "release_price": "0.00",
 *      "discount_code_used": "",
 *      "total_paid": "0.00",
 *      "total_paid_less_tax": "0.00",
 *      "updated_at": "2019-04-23T15:51:12.422Z",
 *      "url": "https://ti.to/tickets/p0oR6QJehlzYdduZw5GMq4w",
 *      "admin_url": "https://ti.to/dddperth/committee-meeting/admin/tickets/p0oR6QJehlzYdduZw5GMq4w",
 *      "release_title": "Childcare",
 *      "release_slug": "m75-uimyfou",
 *      "release_id": 1178292,
 *      "release": {
 *        "id": 1178292,
 *        "title": "Childcare",
 *        "slug": "m75-uimyfou",
 *        "metadata": null
 *      },
 *      "custom": "",
 *      "registration_id": "pKNKp039KHDYqJSp3j3wVRQ",
 *      "registration_slug": "pKNKp039KHDYqJSp3j3wVRQ",
 *      "metadata": null,
 *      "answers": [
 *        {
 *          "question": {
 *            "title": "How old is the Child?",
 *            "description": "",
 *            "id": 1073009
 *          },
 *          "response": "2",
 *          "humanized_response": "2"
 *        },
 *        {
 *          "question": {
 *            "title": "Does the child have any special needs?",
 *            "description": "",
 *            "id": 1073010
 *          },
 *          "response": "No",
 *          "humanized_response": "No"
 *        }
 *      ],
 *      "responses": {
 *        "how-old-is-the-child": "2",
 *        "does-the-child-have-any-special-needs": "No"
 *      },
 *      "upgrade_ids": [
 *        
 *      ],
 *      "registration": {
 *        "url": "https://ti.to/registrations/pKNKp039KHDYqJSp3j3wVRQ",
 *        "admin_url": "https://ti.to/dddperth/committee-meeting/admin/registrations/pKNKp039KHDYqJSp3j3wVRQ",
 *        "total": "0.00",
 *        "currency": "AUD",
 *        "payment_reference": null,
 *        "source": null,
 *        "name": "Rob Moore",
 *        "email": "robertmoore@xxx.com",
 *        "receipt": {
 *          "total": "0.00",
 *          "tax": 0,
 *          "payment_provider": "",
 *          "paid": true,
 *          "receipt_lines": [
 *            {
 *              "total": "0.00",
 *              "quantity": 1,
 *              "tax": 0
 *            },
 *            {
 *              "total": "0.00",
 *              "quantity": 1,
 *              "tax": 0
 *            }
 *          ]
 *        }
 *      }
 *    }
 *  * registration.completed (when all tickets in a registration are completed)
 *
 * Other webhook types:
 *  * checkin.created
 *  * ticket.reassigned
 *  * ticket.unsnoozed
 *  * ticket.unvoided
 *  * ticket.voided
 *  * registration.updated
 *  * registration.marked_as_paid
 *  * registration.cancelled
 */
