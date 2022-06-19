using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SkyWing.RakLib.Generic;

public class RakNetSocket {

    public Socket Socket { get; }
    public UdpState State = new();
    public EndPoint FromEndPoint = new IPEndPoint(IPAddress.Any, 0);
    
    public InternetAddress BindAddress { get; }
    public CancellationToken Token;

    public const int BUFFER_SIZE = Int16.MaxValue;

    public RakNetSocket(InternetAddress bindAddress, CancellationToken token) {
        BindAddress = bindAddress;
        Token = token;
        Socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.ReuseAddress, true);
        
        Socket.Bind(bindAddress.IpEndPoint);
    }

    public void ReadPacket(Action<byte[], IPAddress, int> onPacket,
        Action<Exception> onError) {
        try {
            Socket.BeginReceiveFrom(State.Buffer, 0, BUFFER_SIZE, SocketFlags.None, ref FromEndPoint, ar => {
                var state = (UdpState) ar.AsyncState;
                Socket.EndReceiveFrom(ar, ref FromEndPoint);
                onPacket((byte[]) State.Buffer.Clone(), ((IPEndPoint) FromEndPoint).Address, ((IPEndPoint) FromEndPoint).Port);
            }, State);
        }
        catch (Exception e) {
            onError(e);
        }
    }

    public int WritePacket(byte[] buffer, IPAddress address, int port) {
        return Socket.SendTo(buffer, 0, buffer.Length, SocketFlags.None, new IPEndPoint(address, port));
    }

    public void Close() {
        Socket.Close();
    }

    public class UdpState {
        public byte[] Buffer = new byte[BUFFER_SIZE];
    }
}