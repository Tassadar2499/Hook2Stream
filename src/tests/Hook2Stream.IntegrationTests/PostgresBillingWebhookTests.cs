using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Hook2Stream.Application;
using Hook2Stream.Domain;
using Hook2Stream.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;

namespace Hook2Stream.IntegrationTests;

public sealed class PostgresBillingWebhookTests
{
    [Fact]
    public async Task Actionable_webhook_is_atomic_with_npgsql_retry_execution_strategy()
    {
        var adminConnectionString = Environment.GetEnvironmentVariable("HOOK2STREAM_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(adminConnectionString))
        {
            Assert.False(
                string.Equals(Environment.GetEnvironmentVariable("CI"), "true", StringComparison.OrdinalIgnoreCase),
                "CI must provide HOOK2STREAM_TEST_POSTGRES for the billing webhook PostgreSQL contract.");
            return;
        }

        var databaseName = $"hook2stream_billing_webhook_{Guid.NewGuid():N}";
        var connectionString = new NpgsqlConnectionStringBuilder(adminConnectionString)
        {
            Database = databaseName,
            Pooling = false
        }.ConnectionString;
        await using var admin = new NpgsqlConnection(adminConnectionString);
        await admin.OpenAsync();
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin))
        {
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await using (var migrationDb = CreateDb(connectionString))
            {
                await migrationDb.Database.MigrateAsync();
            }

            var gateway = new MutablePaymentGateway();
            await using var factory = new PostgresBillingApiFactory(connectionString, gateway);
            using var client = factory.CreateClient();
            await Onboard(client);
            Guid workspaceId;
            Guid checkoutId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
                workspaceId = await db.Workspaces.Select(value => value.Id).SingleAsync();
                var checkout = new BillingCheckout
                {
                    WorkspaceId = workspaceId,
                    ProductCode = BillingProducts.ActiveArtist,
                    AmountCents = BillingProducts.AmountCents(BillingProducts.ActiveArtist),
                    IdempotencyKey = "postgres-actionable-webhook",
                    RequestHash = new string('9', 64),
                    State = CheckoutState.Completed,
                    ExternalCustomerId = "cus_postgres_webhook",
                    ExternalSubscriptionId = "sub_postgres_webhook",
                    CheckoutUrl = "https://payments.example.test/postgres-actionable-webhook"
                };
                checkoutId = checkout.Id;
                db.BillingCheckouts.Add(checkout);
                await db.SaveChangesAsync();
            }

            gateway.NextEvent = new PaymentWebhookEvent(
                EventId: "evt-postgres-payment-failed",
                Type: "invoice.payment_failed",
                CheckoutId: checkoutId,
                ExternalSessionId: null,
                ProductCode: BillingProducts.ActiveArtist,
                WorkspaceId: workspaceId,
                ProjectId: null,
                ExternalCustomerId: "cus_postgres_webhook",
                ExternalSubscriptionId: "sub_postgres_webhook",
                ExternalPaymentIntentId: null,
                ExternalInvoiceId: "in_postgres_payment_failed",
                ExternalChargeId: null,
                Disposition: PaymentWebhookDisposition.PaymentFailed,
                OccurredAt: DateTimeOffset.UtcNow,
                PeriodStartsAt: null,
                PeriodEndsAt: null,
                PayloadHash: string.Empty);

            using var response = await client.PostAsJsonAsync(
                "/api/v1/billing/stripe/webhook",
                new { marker = "postgres-actionable-webhook" });

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var verifyScope = factory.Services.CreateAsyncScope();
            var verifyDb = verifyScope.ServiceProvider.GetRequiredService<Hook2StreamDbContext>();
            Assert.Equal(1, await verifyDb.InboxMessages.CountAsync(value =>
                value.Source == "stripe" && value.MessageId == "evt-postgres-payment-failed"));
            Assert.Equal(1, await verifyDb.AuditEvents.CountAsync(value =>
                value.WorkspaceId == workspaceId && value.Action == "billing.provider_payment_failed"));
            Assert.True(
                await verifyDb.BillingCheckouts.Where(value => value.Id == checkoutId)
                    .Select(value => value.Version)
                    .SingleAsync() > 1);
        }
        finally
        {
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static Hook2StreamDbContext CreateDb(string connectionString)
    {
        var options = new DbContextOptionsBuilder<Hook2StreamDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.EnableRetryOnFailure())
            .UseSnakeCaseNamingConvention()
            .Options;
        return new Hook2StreamDbContext(options);
    }

    private static async Task Onboard(HttpClient client)
    {
        using var response = await client.PutAsJsonAsync(
            "/api/v1/account/onboarding",
            new
            {
                workspaceName = "PostgreSQL billing webhook tests",
                acceptTerms = true,
                acceptPrivacy = true,
                termsVersion = "draft-2026-07-16",
                privacyVersion = "draft-2026-07-16",
                displayName = "Test artist"
            });
        response.EnsureSuccessStatusCode();
    }

    private sealed class PostgresBillingApiFactory(
        string connectionString,
        MutablePaymentGateway gateway) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Auth:Mode", "OAuth");
            builder.UseSetting("Storage:AccessKey", "test-access-key");
            builder.UseSetting("Storage:SecretKey", "test-secret-key");
            builder.UseSetting("StorageEncryption:Mode", "Plaintext");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<Hook2StreamDbContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<Hook2StreamDbContext>>();
                services.RemoveAll<Hook2StreamDbContext>();
                services.AddDbContext<Hook2StreamDbContext>(options =>
                    options.UseNpgsql(
                            connectionString,
                            npgsql => npgsql.EnableRetryOnFailure())
                        .UseSnakeCaseNamingConvention());

                services.RemoveAll<IObjectStorage>();
                services.AddSingleton<IObjectStorage, FakeObjectStorage>();
                services.RemoveAll<IPaymentGateway>();
                services.AddSingleton<IPaymentGateway>(gateway);
                services.AddAuthentication(options =>
                    {
                        options.DefaultAuthenticateScheme = TestAuthHandler.SchemeName;
                        options.DefaultChallengeScheme = TestAuthHandler.SchemeName;
                    })
                    .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(
                        TestAuthHandler.SchemeName,
                        _ => { });
            });
        }
    }

    private sealed class MutablePaymentGateway : IPaymentGateway
    {
        public PaymentWebhookEvent? NextEvent { get; set; }

        public Task<PaymentCheckoutResult> CreateCheckoutAsync(
            PaymentCheckoutCommand command,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public PaymentWebhookEvent ParseAndVerifyWebhook(
            ReadOnlySpan<byte> payload,
            string signatureHeader,
            DateTimeOffset now)
        {
            var paymentEvent = NextEvent ?? throw new InvalidOperationException("No webhook was configured.");
            return paymentEvent with
            {
                PayloadHash = Convert.ToHexStringLower(SHA256.HashData(payload))
            };
        }
    }
}
