using System.Net;
using System.Net.Sockets;

namespace SkyWing.RakLib.Generic; 

public class RakNetSocket {
	
	//public Socket Socket { get; private set; }
    public UdpClient Socket { get; private set; }
	public InternetAddress BindAddress { get; }

	public int SendBufferSize {
		get => sendBufferSize;
		set {
			if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
			
			sendBufferSize = value;
			Socket.Client.SendBufferSize = value;
		}
	}

	private int sendBufferSize;

	public int ReceivedBufferSize {
		get => receivedBufferSize;
		set {
			if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
			
			receivedBufferSize = value;
			Socket.Client.ReceiveBufferSize = value;
		}
	}

	private int receivedBufferSize;

	public RakNetSocket(InternetAddress bindAddress) {
		try {
			BindAddress = bindAddress;
            Socket = new UdpClient();
            Socket.DontFragment = false;
            Socket.EnableBroadcast = true;
            const uint iocIn = 0x80000000;
            const int iocVendor = 0x18000000;
            const uint sioUdpConnreset = iocIn | iocVendor | 12;
            Socket.Client.IOControl(unchecked((int) sioUdpConnreset), new[] {Convert.ToByte(false)}, null);
            
            SendBufferSize = 1024 * 1024 * 8;
            ReceivedBufferSize = 1024 * 1024 * 8;
            
            Socket.Client.Bind(new IPEndPoint(IPAddress.Parse(BindAddress.Ip), BindAddress.Port));
        } catch (Exception e) {
			Console.WriteLine(e);
            throw;
        }
	}

    public async Task ReadPacket(Action<byte[], IPAddress, int> onPacket, Action<SocketException> onError) {
        try {
            var received = await Socket.ReceiveAsync();
            var buffer = received.Buffer;
            var endPoint = received.RemoteEndPoint;

            onPacket(buffer, endPoint.Address, endPoint.Port);
        }
        catch (ObjectDisposedException) {
        }
        catch (SocketException e) {
            switch (e.ErrorCode) {
                // 10058 (just regular disconnect while listening)
                case 10058:
                case 10038:
                case 10004:
                    return;
                default:
                    onError(e);
                    break;
            }
        }
    }

    public async Task<int> WritePacket(byte[] buffer, IPAddress address, int port) {
		return await Socket.SendAsync(buffer, new IPEndPoint(address, port));
	}

	public void Close() {
		Socket.Close();
	}
}