namespace DexTor.Creator;

public enum CreatorStep
{
    None,
    PrepareRelease,
    ConfirmErase,
    FormatUsb,
    WriteSystemImage,
    CreateDataImage,
    VerifyUsb,
    ProvisionGuest,
    Complete
}
