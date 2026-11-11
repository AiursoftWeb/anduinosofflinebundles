using Aiursoft.CommandFramework;
using Aiursoft.AnduinOS.OfflineBundles.Handlers;

return await new NestedCommandApp()
    .WithFeature(new BuildHandler())
    .WithFeature(new InspectHandler())
    .WithFeature(new VerifyHandler())
    .RunAsync(args);
