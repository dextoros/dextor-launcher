using DexTor.Provisioner;

// The command lives in ProvisionerCommand so the portable launcher can run the same code
// in its own elevated process; this executable remains for the folder build.
return await ProvisionerCommand.RunAsync(args);
