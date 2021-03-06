﻿using System;
using Neo4j.Driver;
using System.Threading.Tasks;
using System.Text.Json.Serialization;

namespace Neo4j.Driver.Tests.TestBackend
{
    internal class NewDriver : IProtocolObject
    {
        public NewDriverType data { get; set; } = new NewDriverType();
        [JsonIgnore]
        public IDriver Driver { get; set; }

        public class NewDriverType
        {
            public string uri { get; set; }
            public AuthorizationToken authorizationToken { get; set; } = new AuthorizationToken();
        }

        public override async Task Process()
        {
            try
            {
                var authTokenData = data.authorizationToken.data;
                var authToken = AuthTokens.Custom(authTokenData.principal, authTokenData.credentials, authTokenData.realm, authTokenData.scheme);

                Driver = GraphDatabase.Driver(data.uri, authToken);
                await AysncVoidReturn();
            }
            catch (Exception ex)
            {
                throw new Exception($"Failed to Process NewDriver protocol object, failed with - {ex.Message}");
            }
        }

        public override string Response()
        {
            return new Response("Driver", uniqueId).Encode();
        }
    }
}
