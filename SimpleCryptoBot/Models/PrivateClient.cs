using CoinbasePro;
using CoinbasePro.Network.Authentication;
using System;

namespace SimpleCryptoBot.Models
{
    public class PrivateClient : CoinbaseProClient, IDisposable
    {
        public PrivateClient(string name, string email, Authenticator authenticator, bool sandbox) : base(authenticator, sandbox)
        {
            this.Name = name;
            this.Email = email;
        }

        public string Name { get; set; }

        /// <summary>
        /// For reports
        /// </summary>
        public string Email { get; set; }

        public void Dispose()
        {
            if(this.WebSocket?.State == WebSocket4Net.WebSocketState.Open || this.WebSocket?.State == WebSocket4Net.WebSocketState.Connecting)
            {
                this.WebSocket.Stop();

                while(this.WebSocket.State != WebSocket4Net.WebSocketState.Closed)
                {

                }
            }

            this.Name = null;
            this.Email = null;
        }
    }
}
