using TeamSpeak.Query.FileTransfer;
using TeamSpeak.Query.Protocol;

namespace TeamSpeak.Query.Tests.FileTransfer;

/// <summary>Tickets read from answers captured on 6.0.0-beta12.1.</summary>
public class FileTransferTicketTests
{
    private static QueryRecord Record(string line) =>
        QueryResponseParser.Parse($"{line}\nerror id=0 msg=ok").Records[0];

    [Fact]
    public void Reads_an_upload_ticket()
    {
        var ticket = FileTransferTicket.FromRecord(Record(
            "clientftfid=11 serverftfid=1 ftkey=829ebfa0809caf939de658f1fb71dafc port=30033 seekpos=0 proto=0"));

        Assert.Equal((11, 1, "829ebfa0809caf939de658f1fb71dafc", 30033, 0L, 0L), (ticket.ClientTransferId, ticket.ServerTransferId, ticket.Key, ticket.Port, ticket.Size, ticket.SeekPosition));
        Assert.Empty(ticket.Addresses);
    }

    [Fact]
    public void Reads_a_download_ticket_with_its_size_and_any_addresses_the_server_names()
    {
        var ticket = FileTransferTicket.FromRecord(Record(
            "clientftfid=12 serverftfid=2 ftkey=f9f5dbd77d2c16a5f06ac505dfcd5d32 port=30033 size=2062 proto=0 ip=10.0.0.5,\\s192.0.2.1"));

        Assert.Equal(2062, ticket.Size);
        Assert.Equal(["10.0.0.5", "192.0.2.1"], ticket.Addresses);
    }

    [Theory]
    [InlineData("clientftfid=14 status=2051 msg=file\\snot\\sfound size=0", 2051, "file not found")]
    [InlineData("clientftfid=24 status=2050 msg=file\\salready\\sexists size=3145728", 2050, "file already exists")]
    [InlineData("clientftfid=43 status=2054 msg=invalid\\sfile\\spath size=0", 2054, "invalid file path")]
    public void Turns_a_refusal_inside_the_record_into_an_exception(string line, int status, string message)
    {
        // These arrived with error id=0 on the status line, so only the record tells them apart.
        var ex = Assert.Throws<FileTransferRefusedException>(() => FileTransferTicket.FromRecord(Record(line)));

        Assert.Equal((status, message), (ex.Status, ex.ServerMessage));
        Assert.Equal(status, ex.ToQueryError().Id);
    }

    [Fact]
    public void Refuses_a_record_without_a_key()
    {
        Assert.Throws<QueryProtocolException>(() => FileTransferTicket.FromRecord(Record("clientftfid=1 port=30033")));
    }

    [Theory]
    [InlineData("ftkey=abc port=0")]
    [InlineData("ftkey=abc port=70000")]
    [InlineData("ftkey=abc port=30033 size=-1")]
    [InlineData("ftkey=abc port=30033 seekpos=-5")]
    public void Refuses_a_port_size_or_position_no_transfer_could_have(string line) =>
        Assert.Throws<QueryProtocolException>(() => FileTransferTicket.FromRecord(Record(line)));
}