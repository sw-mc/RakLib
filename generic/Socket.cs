using System.Net;
using System.Net.Sockets;

namespace SkyWing.RakLib.Generic; 

public class RakNetSocket {
	
	public Socket Socket { get; private set; }
	public InternetAddress BindAddress { get; }

	public int SendBufferSize {
		get => sendBufferSize;
		set {
			if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
			
			sendBufferSize = value;
			Socket.SendBufferSize = value;
		}
	}

	private int sendBufferSize;

	public int ReceivedBufferSize {
		get => receivedBufferSize;
		set {
			if (value < 0) throw new ArgumentOutOfRangeException(nameof(value));
			
			receivedBufferSize = value;
			Socket.ReceiveBufferSize = value;
		}
	}

	private int receivedBufferSize;

	public RakNetSocket(InternetAddress bindAddress) {
		try {
			BindAddress = bindAddress;
			Socket = new Socket(SocketType.Stream, ProtocolType.Udp);
			Socket.Bind(new IPEndPoint(IPAddress.Parse(BindAddress.Ip), BindAddress.Port));
			SendBufferSize = 1024 * 1024 * 8;
			ReceivedBufferSize = 1024 * 1024 * 8;
			Socket.Blocking = false;
		} catch (Exception e) {
			Console.WriteLine(e);
            throw;
        }
	}

    public void ReadPacket(Action<byte[], IPAddress, int> onPacket, Action<SocketException> onError) {
        var buffer = new byte[1024 * 1024 * 8];
        var remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);

        var args = new SocketAsyncEventArgs {
            RemoteEndPoint = remoteEndPoint
        };
        args.SetBuffer(buffer, 0, buffer.Length);
        args.Completed += (sender, e) => {
            if (e.SocketError != SocketError.Success) {
                onError(new SocketException((int) e.SocketError));
                return;
            }
            onPacket(buffer, remoteEndPoint.Address, remoteEndPoint.Port);
        };

        Socket.ReceiveFromAsync(args);
    }

    public async Task<int> WritePacket(byte[] buffer, IPAddress address, int port) {
		return await Socket.SendToAsync(buffer, SocketFlags.None, new IPEndPoint(address, port));
	}

	public void Close() {
		Socket.Close();
	}
}