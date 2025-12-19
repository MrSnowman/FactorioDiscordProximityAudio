using System.ComponentModel.Design;
using System.Net.NetworkInformation;
using Client.Services;
using Client.VisualComponents;
using Serilog;

namespace Client;

public sealed partial class Main : Form
{
    public static           ServicesMarshal  servicesMarshal  = null!;
    private static readonly ServiceContainer ServiceContainer = new();

    public static string? targetIp;
    public static ushort  targetPort;
    public static bool    useVerboseLogging;

    private string _connectButtonText = string.Empty;
    private bool   _hasConnection;

    public Main()
    {
        InitializeComponent();

        ServiceContainer.AddService(typeof(FactorioFileWatcherService),    new FactorioFileWatcherService());
        ServiceContainer.AddService(typeof(DiscordPipeService),            new DiscordPipeService());
        ServiceContainer.AddService(typeof(PositionTransferClientService), new PositionTransferClientService());
        ServiceContainer.AddService(typeof(PositionTransferHostService),   new PositionTransferHostService());
        ServiceContainer.AddService(typeof(VolumeUpdaterService),          new VolumeUpdaterService());

        servicesMarshal = new ServicesMarshal(ServiceContainer);
    }

    private void Main_Load(object sender, EventArgs e)
    {
        isClient.Checked = true;
        portTextbox.Text = PositionTransferHostService.StartingPort.ToString();

        Log.Logger = new LoggerConfiguration()
                     .WriteTo.RichTextBox(logTextbox)
                     .WriteTo.File("log.txt", rollingInterval: RollingInterval.Day)
                     .CreateLogger();

        toolTip1.SetToolTip(ipTextbox, "IP Address of the host to connect to. Defaults to 127.0.0.1 for host mode.");
        toolTip1.SetToolTip(
            portTextbox,
            $"Port of the host to connect to. Defaults to {PositionTransferHostService.StartingPort} for host mode.");

        toolTip1.SetToolTip(isHost,   "Host to manage player positions. A client connection will also be made.");
        toolTip1.SetToolTip(isClient, "Connect to a host to send and receive player positions.");

        servicesMarshal.OnStarted = () =>
        {
            if (connectButton.InvokeRequired)
                connectButton.Invoke(OnConnectionComplete);
            else
                OnConnectionComplete();
        };

        servicesMarshal.OnStopped = () =>
        {
            if (connectButton.InvokeRequired)
                connectButton.Invoke(OnConnectionCancelled);
            else
                OnConnectionCancelled();
        };
    }

    private async void connectButton_Click(object sender, EventArgs e)
    {
        try
        {
            var hadConnection = _hasConnection;
            _hasConnection = !_hasConnection;

            if (hadConnection)
                await DisconnectServices();
            else
                await ConnectServices();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Error connecting/disconnecting: {Message}", ex.Message);
        }
    }

    private async Task ConnectServices()
    {
        _hasConnection = true;

        var isDestinationValid = isHost.Checked
            ? TryValidateHostWebSocketDestination(out targetIp, out targetPort)
            : TryValidateClientWebSocketDestination(out targetIp, out targetPort);

        if (!isDestinationValid)
        {
            OnConnectionCancelled();
            return;
        }

        ipTextbox.Text   = targetIp;
        portTextbox.Text = targetPort.ToString();

        connectButton.Text    = "Connecting...";
        connectButton.Enabled = false;

        Type[][] serviceTypes = isHost.Checked
            ?
            [
                [typeof(DiscordPipeService), typeof(FactorioFileWatcherService)],
                [typeof(PositionTransferHostService)],
                [typeof(PositionTransferClientService)],
                [typeof(VolumeUpdaterService)]
            ]
            :
            [
                [typeof(DiscordPipeService), typeof(FactorioFileWatcherService)],
                [typeof(PositionTransferClientService)],
                [typeof(VolumeUpdaterService)]
            ];

        await servicesMarshal.StartAsync(serviceTypes, Program.ApplicationExitCancellationToken);
    }

    private void OnConnectionComplete()
    {
        connectButton.Text    = "Disconnect";
        connectButton.Enabled = true;
        ipTextbox.Enabled     = portTextbox.Enabled = isClient.Enabled = isHost.Enabled = !_hasConnection;
    }

    private void OnConnectionCancelled()
    {
        connectButton.Text    = _connectButtonText;
        connectButton.Enabled = true;
        _hasConnection        = false;
        ipTextbox.Enabled     = portTextbox.Enabled = isClient.Enabled = isHost.Enabled = !_hasConnection;
    }

    private async Task DisconnectServices()
    {
        connectButton.Text = "Disconnecting...";
        await servicesMarshal.StopAsync();
    }

    private bool TryValidateHostWebSocketDestination(out string address, out ushort port)
    {
        port    = 0;
        address = "127.0.0.1";

        var portsInUse = AddressUtility.GetActiveUdpPorts();

        if (!ushort.TryParse(portTextbox.Text, out port) ||
            port is <= 0 or >= ushort.MaxValue           ||
            portsInUse.Contains(port))
        {
            Log.Warning("Port is left empty or unavailable. Finding a port...");

            var openPort =
                AddressUtility.FindFirstAvailablePort(portsInUse, PositionTransferHostService.StartingPort,
                                                      PositionTransferHostService.CheckPortCount);
            if (openPort == 0)
            {
                Log.Error("Failed to find an available port.");
                return false;
            }

            if (!AddressUtility.CheckUrlReservation(port))
            {
                Log.Information("Reserving port {Port} for websocket...", port);

                if (!AddressUtility.AddUrlReservation(port))
                    Log.Error("Failed to reserve port. Websocket connection may fail.");
            }

            port = openPort;
        }

        return true;
    }

    private bool TryValidateClientWebSocketDestination(out string address, out ushort port)
    {
        port    = 0;
        address = string.Empty;

        if (string.IsNullOrEmpty(ipTextbox.Text))
        {
            Log.Error("IP Address cannot be null.");
            return false;
        }

        if (string.IsNullOrEmpty(portTextbox.Text))
        {
            Log.Error("Port cannot be null.");
            return false;
        }

        if (!ushort.TryParse(portTextbox.Text, out port))
        {
            Log.Error("Port is invalid.");
            return false;
        }

        address = ipTextbox.Text;
        return true;
    }

    private void isClient_CheckedChanged(object sender, EventArgs e)
    {
        if (!isClient.Checked)
            return;

        ipTextbox.ReadOnly = false;
        connectButton.Text = _connectButtonText = "Connect";
    }

    private void isHost_CheckedChanged(object sender, EventArgs e)
    {
        if (!isHost.Checked)
            return;

        ipTextbox.ReadOnly = true;
        connectButton.Text = _connectButtonText = "Host";
    }

    private void AddressPasted(object sender, ClipboardEventArgs e)
    {
        var args = e.ClipboardText.Split(':');
        if (args.Length != 2)
        {
            ((PastableTextBox)sender).Text = e.ClipboardText;
            return;
        }

        ipTextbox.Text   = args[0];
        portTextbox.Text = args[1];
    }

    private void OnIPTextChanged(object sender, EventArgs e)
    {
        if (ipTextbox.Text.Length < 1)
            return;
        
        if (ipTextbox.Text[^1] != ':')
            return;

        ipTextbox.Text = ipTextbox.Text[..^1];
        portTextbox.Focus();
    }

    private void verboseLogging_CheckedChanged(object sender, EventArgs e)
    {
        useVerboseLogging = verboseLogging.Checked;
    }

    private async void Main_FormClosing(object sender, FormClosingEventArgs e)
    {
        try
        {
            if (e.CloseReason == CloseReason.UserClosing)
                e.Cancel = true;

            await servicesMarshal.StopAsync();
            ServiceContainer.Dispose();
            await Program.ApplicationExitCancellationTokenSource.CancelAsync();
        }
        catch (Exception)
        {
            // ignored
        }
        finally
        {
            Application.Exit();
        }
    }
}
