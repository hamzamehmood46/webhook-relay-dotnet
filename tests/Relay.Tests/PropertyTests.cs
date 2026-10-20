using Relay.Engine;

namespace Relay.Tests;

public class PropertyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void After_random_activity_the_bookkeeping_always_holds(int seed)
    {
        var random = new Random(seed);
        var r = new Rig();
        r.Hub.AddRoute(r.Source.Id, r.Audit.Id, ["invoice.*"]);
        var ids = new List<string>();
        var accepted = 0;

        r.Transport.Handler = request => random.Next(10) switch
        {
            < 5 => Rig.Ok,
            < 7 => Rig.Down,
            7 => new TransportResult(429, null, TimeSpan.FromMinutes(random.Next(1, 90))),
            8 => new TransportResult(400),
            _ => new TransportResult(null, "Timed out"),
        };

        for (var step = 0; step < 500; step++)
        {
            switch (random.Next(9))
            {
                case 0:
                case 1:
                case 2:
                    var id = ids.Count > 0 && random.Next(3) == 0 ? ids[random.Next(ids.Count)] : "evt" + ids.Count;
                    if (!ids.Contains(id))
                    {
                        ids.Add(id);
                    }

                    var type = random.Next(2) == 0 ? "invoice.paid" : "customer.created";
                    if (r.Post(id, type).Outcome == IngestOutcome.Accepted)
                    {
                        accepted++;
                    }

                    break;
                case 3:
                case 4:
                    r.Run();
                    break;
                case 5:
                    r.Skip(TimeSpan.FromMinutes(random.Next(1, 120)));
                    break;
                case 6:
                    var dead = r.Hub.Deliveries(DeliveryStatus.DeadLettered, limit: 50);
                    if (dead.Count > 0)
                    {
                        r.Hub.ReplayDelivery(dead[random.Next(dead.Count)].Id);
                    }

                    break;
                case 7:
                    r.Hub.SetDestinationEnabled(random.Next(2) == 0 ? r.Billing.Id : r.Audit.Id, random.Next(4) != 0);
                    break;
                case 8 when random.Next(5) == 0:
                    r.Hub.ReplayDeadLetters(r.Billing.Id);
                    break;
            }

            foreach (var d in r.Hub.Deliveries(limit: 1000))
            {
                Assert.True(d.Attempts <= 6, "never more attempts than the limit");
                Assert.Equal(d.Attempts, d.History.Count);
                Assert.Equal(d.Attempts, d.History.Select(a => a.Number).Distinct().Count());
                if (d.Status == DeliveryStatus.Succeeded)
                {
                    Assert.InRange(d.History[^1].StatusCode ?? 0, 200, 299);
                    Assert.NotNull(d.CompletedAt);
                }

                if (d.Status == DeliveryStatus.Pending)
                {
                    Assert.Equal(0, d.Attempts);
                }

                if (d.Status == DeliveryStatus.Retrying)
                {
                    Assert.InRange(d.Attempts, 1, 5);
                }

                Assert.Equal(d.IsOpen, d.CompletedAt is null);
            }
        }

        // Each provider event exists once, and each (event, destination) pair has exactly one original delivery.
        var events = r.Hub.Events(limit: 500);
        Assert.Equal(accepted, events.Count);
        Assert.Equal(events.Count, events.Select(e => e.ProviderEventId).Distinct().Count());
        var originals = r.Hub.Deliveries(limit: 1000).Where(d => d.ReplayOf is null).GroupBy(d => (d.EventId, d.DestinationId));
        Assert.All(originals, g => Assert.Single(g));

        // Customer events never reach the audit log, because its route only wants invoice.*.
        foreach (var d in r.Hub.Deliveries(limit: 1000).Where(d => d.DestinationId == r.Audit.Id))
        {
            Assert.StartsWith("invoice.", r.Hub.Event(d.EventId)!.Type);
        }
    }
}
