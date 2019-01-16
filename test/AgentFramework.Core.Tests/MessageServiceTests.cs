﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AgentFramework.Core.Contracts;
using AgentFramework.Core.Exceptions;
using AgentFramework.Core.Extensions;
using AgentFramework.Core.Messages;
using AgentFramework.Core.Messages.Connections;
using AgentFramework.Core.Messages.Routing;
using AgentFramework.Core.Models;
using AgentFramework.Core.Models.Connections;
using AgentFramework.Core.Models.Records;
using AgentFramework.Core.Models.Records.Search;
using AgentFramework.Core.Runtime;
using Hyperledger.Indy.CryptoApi;
using Hyperledger.Indy.DidApi;
using Hyperledger.Indy.WalletApi;
using Microsoft.Extensions.Logging;
using Moq;
using Moq.Protected;
using Newtonsoft.Json.Linq;
using Xunit;

namespace AgentFramework.Core.Tests
{
    public class MessageServiceTests : IAsyncLifetime
    {
        private string Config = "{\"id\":\"" + Guid.NewGuid() + "\"}";
        private const string WalletCredentials = "{\"key\":\"test_wallet_key\"}";

        private Wallet _wallet;

        private readonly IMessageService _messagingService;

        private readonly ConcurrentBag<HttpRequestMessage> _messages = new ConcurrentBag<HttpRequestMessage>();

        public MessageServiceTests()
        {
            var handlerMock = new Mock<HttpMessageHandler>(MockBehavior.Strict);
            handlerMock
                .Protected()
                // Setup the PROTECTED method to mock
                .Setup<Task<HttpResponseMessage>>(
                    "SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>()
                )
                // prepare the expected response of the mocked http call
                .ReturnsAsync(new HttpResponseMessage()
                {
                    StatusCode = HttpStatusCode.OK,
                    Content = new StringContent(""),
                })
                .Callback((HttpRequestMessage request, CancellationToken cancellationToken) =>
                {
                    _messages.Add(request);
                })
                .Verifiable();

            // use real http client with mocked handler here
            var httpClient = new HttpClient(handlerMock.Object);

            var mockConnectionService = new Mock<IConnectionService>();
            mockConnectionService.Setup(_ => _.ListAsync(It.IsAny<Wallet>(), It.IsAny<ISearchQuery>(), It.IsAny<int>()))
                .Returns(Task.FromResult(new List<ConnectionRecord> { new ConnectionRecord() }));

            _messagingService = new DefaultMessageService(mockConnectionService.Object, 
                new Mock<ILogger<DefaultMessageService>>().Object, httpClient);
        }

        public async Task InitializeAsync()
        {
            try
            {
                await Wallet.CreateWalletAsync(Config, WalletCredentials);
            }
            catch (WalletExistsException)
            {
                // OK
            }

            _wallet = await Wallet.OpenWalletAsync(Config, WalletCredentials);
        }

        public async Task DisposeAsync()
        {
            if (_wallet != null) await _wallet.CloseAsync();
            await Wallet.DeleteWalletAsync(Config, WalletCredentials);
        }

        [Fact]
        public async Task CanSendMessage()
        {
            var my = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");
            var their = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");
            var agency = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");

            var connection = new ConnectionRecord
            {
                MyDid = my.Did,
                MyVk = my.VerKey,
                Alias = new ConnectionAlias
                {
                    Name = "Test"
                },
                Endpoint = new AgentEndpoint
                {
                    Uri = "https://mock.com",
                    Verkey = agency.VerKey
                },
                TheirDid = their.Did,
                TheirVk = their.VerKey
            };

            await _messagingService.SendAsync(_wallet, new ConnectionRequestMessage(), connection);

            var httpMessage = _messages.FirstOrDefault();

            Assert.True(httpMessage != null);

            Assert.True(httpMessage.Method == HttpMethod.Post);
            Assert.True(httpMessage.RequestUri == new Uri("https://mock.com"));
            Assert.True(httpMessage.Content.Headers.ContentType.ToString() == DefaultMessageService.AgentWireMessageMimeType);

            var body = await httpMessage.Content.ReadAsByteArrayAsync();

            var outerWireMessage = body.ToObject<AgentWireMessage>();
            var innerWireMessage =
                (await Crypto.AnonDecryptAsync(
                    _wallet,
                    outerWireMessage.To,
                    outerWireMessage.Message.GetBytesFromBase64()))
                .ToObject<ForwardMessage>()
                .Message
                .GetBytesFromBase64()
                .ToObject<AgentWireMessage>();

            var authDecrypted = await Crypto.AuthDecryptAsync(
                _wallet, innerWireMessage.To, innerWireMessage.Message.GetBytesFromBase64());

            var message = JObject.Parse(authDecrypted.MessageData.GetUTF8String());
            
            Assert.Equal(message["@type"].ToString(), MessageTypes.ConnectionRequest);
            Assert.Equal(my.VerKey, authDecrypted.TheirVk);

            var request = message.ToObject<ConnectionRequestMessage>();
            Assert.NotNull(request);
        }

        [Fact]
        public async Task CanRecieveAuthCryptMessage()
        {
            var my = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");
            var their = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");

            var message = new AgentWireMessage
                {
                    To = their.VerKey,
                    From = my.VerKey,
                    Message = (await Crypto.AuthCryptAsync(
                            _wallet,
                            my.VerKey,
                            their.VerKey,
                            new ConnectionRequestMessage().ToByteArray()))
                        .ToBase64String()
                }
                .ToByteArray();

            var context = await _messagingService.RecieveAsync(new AgentContext { Wallet = _wallet}, message);

            Assert.True(context.MessageType == MessageTypes.ConnectionRequest);
            Assert.True(context.MessageData.Length != 0);
            Assert.NotNull(context.Connection);
            Assert.NotNull(context.GetMessage<ConnectionRequestMessage>());
        }

        [Fact]
        public async Task CanRecieveAnonCryptMessage()
        {
            var my = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");
            var their = await Did.CreateAndStoreMyDidAsync(_wallet, "{}");

            var message = new AgentWireMessage
                {
                    To = their.VerKey,
                    From = my.VerKey,
                    Message = (await Crypto.AnonCryptAsync(
                            their.VerKey,
                            new ConnectionRequestMessage().ToByteArray()))
                        .ToBase64String()
                }
                .ToByteArray();

            var context = await _messagingService.RecieveAsync(new AgentContext { Wallet = _wallet }, message);

            Assert.True(context.MessageType == MessageTypes.ConnectionRequest);
            Assert.True(context.MessageData.Length != 0);
            Assert.NotNull(context.GetMessage<ConnectionRequestMessage>());
        }

        [Fact]
        public async Task RecieveThrowsCannotUnpackException()
        {
            var dummyConfig = "{\"id\":\"" + Guid.NewGuid() + "\"}";
            try
            {
                await Wallet.CreateWalletAsync(dummyConfig, WalletCredentials);
            }
            catch (WalletExistsException)
            {
                // OK
            }

            var dummyWallet = await Wallet.OpenWalletAsync(dummyConfig, WalletCredentials);
            var my = await Did.CreateAndStoreMyDidAsync(dummyWallet, "{}");
            var their = await Did.CreateAndStoreMyDidAsync(dummyWallet, "{}");

            var message = new AgentWireMessage
                {
                    To = their.VerKey,
                    From = my.VerKey,
                    Message = (await Crypto.AnonCryptAsync(
                            their.VerKey,
                            new ConnectionRequestMessage().ToByteArray()))
                        .ToBase64String()
                }
                .ToByteArray();

            var ex = await Assert.ThrowsAsync<AgentFrameworkException>(async () => await _messagingService.RecieveAsync(new AgentContext { Wallet = _wallet }, message));
            Assert.True(ex.ErrorCode == ErrorCode.MessageUnpackError);

            if (dummyWallet != null) await dummyWallet.CloseAsync();
            await Wallet.DeleteWalletAsync(dummyConfig, WalletCredentials);
        }
    }
}