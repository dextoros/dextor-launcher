using FluentAssertions;
using System.Text.Json;
using Xunit;

using DexTor.Contracts;

namespace DexTor.Contracts.Tests;

public class MessageSerializerTests
{
    [Fact]
    public void SerializeAndDeserializePayloadRoundtrip()
    {
        var payload = JsonDocument.Parse("{\"status\":\"ok\"}");
        var envelope = new MessageEnvelope
        {
            Kind = MessageKind.HostEvent,
            Type = "session-state",
            Payload = payload.RootElement.Clone()
        };

        var serialized = MessageSerializer.Serialize(envelope);
        var parsed = MessageSerializer.Deserialize(serialized);

        parsed.Should().NotBeNull();
        parsed!.Kind.Should().Be(MessageKind.HostEvent);
        parsed.Type.Should().Be("session-state");
    }

    [Fact]
    public void UsbProvisioningRequestAndResultRoundtripPreservePhysicalIdentity()
    {
        var request = new UsbProvisioningRequest(
            Guid.NewGuid(),
            new UsbPhysicalIdentity(7, "usb-id", "serial", 64L * 1024 * 1024 * 1024, "USB", "Test USB", false, false, false),
            @"C:\image\dextor.iso",
            new string('a', 64),
            32L * 1024 * 1024 * 1024,
            true);

        var parsedRequest = UsbProvisioningSerializer.DeserializeRequest(
            UsbProvisioningSerializer.SerializeRequest(request));
        parsedRequest.Should().BeEquivalentTo(request);

        var result = new UsbProvisioningResult(
            request.RequestId,
            UsbProvisioningStatus.Complete,
            "ready",
            @"R:\",
            @"R:\DEXTOR\system\slot-a\system.img",
            @"R:\DEXTOR\storage\data.img",
            new string('a', 64));
        var parsedResult = UsbProvisioningSerializer.DeserializeResult(
            UsbProvisioningSerializer.SerializeResult(result));
        parsedResult.Should().BeEquivalentTo(result);
        parsedResult!.Success.Should().BeTrue();

        var progress = new UsbProvisioningProgress(request.RequestId, UsbProvisioningStage.WritingSystemImage, DateTimeOffset.UtcNow);
        UsbProvisioningSerializer.DeserializeProgress(UsbProvisioningSerializer.SerializeProgress(progress))
            .Should().BeEquivalentTo(progress);
    }
    [Fact]
    public void ProgressCarriesBytesAndStillReadsFilesWrittenBeforeItDid()
    {
        var progress = new UsbProvisioningProgress(Guid.NewGuid(), UsbProvisioningStage.CopyingWindowsImage, DateTimeOffset.UtcNow, 1_234_567, 4_095_651_840);
        UsbProvisioningSerializer.DeserializeProgress(UsbProvisioningSerializer.SerializeProgress(progress))
            .Should().BeEquivalentTo(progress);

        var legacy = UsbProvisioningSerializer.DeserializeProgress(
            "{\"requestId\":\"00000000-0000-0000-0000-000000000001\",\"stage\":2,\"updatedUtc\":\"2026-09-24T10:00:00+00:00\"}");
        legacy!.Stage.Should().Be(UsbProvisioningStage.WritingSystemImage);
        legacy.BytesDone.Should().Be(0);
        legacy.BytesTotal.Should().Be(0);
    }

    [Fact]
    public void SessionStartRequestRoundtripPreservesTempAndUsbSources()
    {
        var request = new SessionStartRequest(
            Guid.NewGuid(),
            @"C:\Users\Test\AppData\Local\Temp\DexTor\LiveImage\dextor.iso",
            @"R:\DEXTOR\system\slot-a\system.img",
            @"R:\DEXTOR\storage\data.img",
            new string('b', 64),
            4096,
            4);

        var parsed = VmSessionSerializer.DeserializeStartRequest(
            VmSessionSerializer.SerializeStartRequest(request));

        parsed.Should().BeEquivalentTo(request);
    }

    [Fact]
    public void SessionStartRequestRoundtripPreservesTheGuestScreen()
    {
        var request = new SessionStartRequest(
            Guid.NewGuid(), @"C:\a.iso", @"R:\s.img", @"R:\d.img", new string('c', 64), 6144, 6,
            GuestLocale: "ru_RU.UTF-8", BuildChannel: "test",
            DisplayWidth: 2560, DisplayHeight: 1377, DisplayScalePercent: 150);

        var parsed = VmSessionSerializer.DeserializeStartRequest(VmSessionSerializer.SerializeStartRequest(request));

        parsed.Should().BeEquivalentTo(request);
    }
}
