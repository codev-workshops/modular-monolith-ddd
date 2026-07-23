using System.Globalization;
using Autofac.Extensions.DependencyInjection;
using MeetingsSystemClock = CompanyName.MyMeetings.Modules.Meetings.Domain.SharedKernel.SystemClock;
using PaymentsSystemClock = CompanyName.MyMeetings.Modules.Payments.Domain.SeedWork.SystemClock;

namespace CompanyName.MyMeetings.API
{
    public class Program
    {
        public static void Main(string[] args)
        {
            FreezeClockIfConfigured();
            CreateWebHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateWebHostBuilder(string[] args)
        {
            return Host.CreateDefaultBuilder(args)
                .UseServiceProviderFactory(new AutofacServiceProviderFactory())
                .ConfigureWebHostDefaults(
                    webBuilder => { webBuilder.UseStartup<Startup>(); });
        }

        /// <summary>
        /// Opt-in determinism hook for the parity baseline: when <c>PARITY_FROZEN_CLOCK</c> is set to an
        /// ISO-8601 timestamp, freeze both module clocks so time-relative state (e.g. subscription
        /// expiry) is reproducible. No-op in normal operation, so production behaviour is unchanged.
        /// </summary>
        private static void FreezeClockIfConfigured()
        {
            var frozen = Environment.GetEnvironmentVariable("PARITY_FROZEN_CLOCK");
            if (!string.IsNullOrWhiteSpace(frozen)
                && DateTime.TryParse(frozen, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date))
            {
                MeetingsSystemClock.Set(date);
                PaymentsSystemClock.Set(date);
            }
        }
    }
}