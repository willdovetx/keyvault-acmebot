﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

using ACMESharp.Authorizations;
using ACMESharp.Protocol;
using ACMESharp.Protocol.Resources;

using AzureKeyVault.LetsEncrypt.Internal;

using DnsClient;

using Microsoft.Azure.KeyVault;
using Microsoft.Azure.KeyVault.Models;
using Microsoft.Azure.Management.Dns;
using Microsoft.Azure.Management.Dns.Models;
using Microsoft.Azure.Services.AppAuthentication;
using Microsoft.Azure.WebJobs;
using Microsoft.Rest;

using Newtonsoft.Json;

namespace AzureKeyVault.LetsEncrypt
{
    public class SharedFunctions : ISharedFunctions
    {
        private const string InstanceIdKey = "InstanceId";

        private static readonly HttpClient _httpClient = new HttpClient();
        private static readonly HttpClient _acmeHttpClient = new HttpClient { BaseAddress = new Uri("https://acme-v02.api.letsencrypt.org/") };

        private static readonly LookupClient _lookupClient = new LookupClient { UseCache = false };

        [FunctionName(nameof(IssueCertificate))]
        public async Task IssueCertificate([OrchestrationTrigger] DurableOrchestrationContext context)
        {
            var dnsNames = context.GetInput<string[]>();

            var proxy = context.CreateActivityProxy<ISharedFunctions>();

            // 前提条件をチェック
            await proxy.Dns01Precondition(dnsNames);

            // 新しく ACME Order を作成する
            var orderDetails = await proxy.Order(dnsNames);

            // 複数の Authorizations を処理する
            var challenges = new List<ChallengeResult>();

            foreach (var authorization in orderDetails.Payload.Authorizations)
            {
                // ACME Challenge を実行
                var result = await proxy.Dns01Authorization((authorization, context.ParentInstanceId));

                // Azure DNS で正しくレコードが引けるか確認
                await proxy.CheckDnsChallenge(result);

                challenges.Add(result);
            }

            // ACME Answer を実行
            await proxy.AnswerChallenges(challenges);

            // Order のステータスが ready になるまで 60 秒待機
            await proxy.CheckIsReady(orderDetails);

            await proxy.FinalizeOrder((dnsNames, orderDetails));
        }

        [FunctionName(nameof(GetCertificates))]
        public async Task<IList<CertificateBundle>> GetCertificates([ActivityTrigger] DateTime currentDateTime)
        {
            var keyVaultClient = CreateKeyVaultClient();

            var certificates = await keyVaultClient.GetCertificatesAsync(Settings.Default.VaultBaseUrl);

            var list = certificates.Where(x => x.Tags != null && x.Tags.TryGetValue("Issuer", out var issuer) && issuer == "letsencrypt.org")
                                   .Where(x => (x.Attributes.Expires.Value - currentDateTime).TotalDays < 30)
                                   .ToArray();

            var bundles = new List<CertificateBundle>();

            foreach (var item in list)
            {
                bundles.Add(await keyVaultClient.GetCertificateAsync(item.Id));
            }

            return bundles;
        }

        [FunctionName(nameof(Order))]
        public async Task<OrderDetails> Order([ActivityTrigger] string[] hostNames)
        {
            var acme = await CreateAcmeClientAsync();

            return await acme.CreateOrderAsync(hostNames);
        }

        [FunctionName(nameof(Dns01Precondition))]
        public async Task Dns01Precondition([ActivityTrigger] string[] hostNames)
        {
            var dnsClient = await CreateDnsManagementClientAsync();

            // Azure DNS が存在するか確認
            var zones = await dnsClient.Zones.ListAsync();

            foreach (var hostName in hostNames)
            {
                if (!zones.Any(x => hostName.EndsWith(x.Name)))
                {
                    throw new InvalidOperationException($"Azure DNS zone \"{hostName}\" is not found");
                }
            }
        }

        [FunctionName(nameof(Dns01Authorization))]
        public async Task<ChallengeResult> Dns01Authorization([ActivityTrigger] (string, string) input)
        {
            var (authzUrl, instanceId) = input;

            var acme = await CreateAcmeClientAsync();

            var authz = await acme.GetAuthorizationDetailsAsync(authzUrl);

            // DNS-01 Challenge の情報を拾う
            var challenge = authz.Challenges.First(x => x.Type == "dns-01");

            var challengeValidationDetails = AuthorizationDecoder.ResolveChallengeForDns01(authz, challenge, acme.Signer);

            // Azure DNS の TXT レコードを書き換え
            var dnsClient = await CreateDnsManagementClientAsync();

            var zone = (await dnsClient.Zones.ListAsync()).First(x => challengeValidationDetails.DnsRecordName.EndsWith(x.Name));

            var resourceId = ParseResourceId(zone.Id);

            // Challenge の詳細から Azure DNS 向けにレコード名を作成
            var acmeDnsRecordName = challengeValidationDetails.DnsRecordName.Replace("." + zone.Name, "");

            RecordSet recordSet;

            try
            {
                recordSet = await dnsClient.RecordSets.GetAsync(resourceId["resourceGroups"], zone.Name, acmeDnsRecordName, RecordType.TXT);
            }
            catch
            {
                recordSet = null;
            }

            if (recordSet != null)
            {
                if (recordSet.Metadata == null || !recordSet.Metadata.TryGetValue(InstanceIdKey, out var dnsInstanceId) || dnsInstanceId != instanceId)
                {
                    recordSet.Metadata = new Dictionary<string, string>
                    {
                        { InstanceIdKey, instanceId }
                    };

                    recordSet.TxtRecords.Clear();
                }

                recordSet.TTL = 60;

                // 既存の TXT レコードに値を追加する
                recordSet.TxtRecords.Add(new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue }));
            }
            else
            {
                // 新しく TXT レコードを作成する
                recordSet = new RecordSet
                {
                    TTL = 60,
                    Metadata = new Dictionary<string, string>
                    {
                        { InstanceIdKey, instanceId }
                    },
                    TxtRecords = new[]
                    {
                        new TxtRecord(new[] { challengeValidationDetails.DnsRecordValue })
                    }
                };
            }

            await dnsClient.RecordSets.CreateOrUpdateAsync(resourceId["resourceGroups"], zone.Name, acmeDnsRecordName, RecordType.TXT, recordSet);

            return new ChallengeResult
            {
                Url = challenge.Url,
                DnsRecordName = challengeValidationDetails.DnsRecordName,
                DnsRecordValue = challengeValidationDetails.DnsRecordValue
            };
        }

        [FunctionName(nameof(CheckDnsChallenge))]
        public async Task CheckDnsChallenge([ActivityTrigger] ChallengeResult challenge)
        {
            // 実際に ACME の TXT レコードを引いて確認する
            var queryResult = await _lookupClient.QueryAsync(challenge.DnsRecordName, QueryType.TXT);

            var txtRecords = queryResult.Answers
                                       .OfType<DnsClient.Protocol.TxtRecord>()
                                       .ToArray();

            // レコードが存在しなかった場合はエラー
            if (txtRecords.Length == 0)
            {
                throw new RetriableActivityException($"{challenge.DnsRecordName} did not resolve.");
            }

            // レコードに今回のチャレンジが含まれていない場合もエラー
            if (!txtRecords.Any(x => x.Text.Contains(challenge.DnsRecordValue)))
            {
                throw new RetriableActivityException($"{challenge.DnsRecordName} value is not correct.");
            }
        }

        [FunctionName(nameof(CheckIsReady))]
        public async Task CheckIsReady([ActivityTrigger] OrderDetails orderDetails)
        {
            var acme = await CreateAcmeClientAsync();

            orderDetails = await acme.GetOrderDetailsAsync(orderDetails.OrderUrl, orderDetails);

            if (orderDetails.Payload.Status == "pending")
            {
                // pending の場合はリトライする
                throw new RetriableActivityException("ACME domain validation is pending.");
            }

            if (orderDetails.Payload.Status == "invalid")
            {
                // invalid の場合は最初から実行が必要なので失敗させる
                throw new InvalidOperationException("Invalid order status. Required retry at first.");
            }
        }

        [FunctionName(nameof(AnswerChallenges))]
        public async Task AnswerChallenges([ActivityTrigger] IList<ChallengeResult> challenges)
        {
            var acme = await CreateAcmeClientAsync();

            // Answer の準備が出来たことを通知
            foreach (var challenge in challenges)
            {
                await acme.AnswerChallengeAsync(challenge.Url);
            }
        }

        [FunctionName(nameof(FinalizeOrder))]
        public async Task FinalizeOrder([ActivityTrigger] (string[], OrderDetails) input)
        {
            var (hostNames, orderDetails) = input;

            var certificateName = hostNames[0].Replace("*", "wildcard").Replace(".", "-");

            var keyVaultClient = CreateKeyVaultClient();

            byte[] csr;

            try
            {
                // Key Vault を使って CSR を作成
                var request = await keyVaultClient.CreateCertificateAsync(Settings.Default.VaultBaseUrl, certificateName, new CertificatePolicy
                {
                    X509CertificateProperties = new X509CertificateProperties
                    {
                        SubjectAlternativeNames = new SubjectAlternativeNames(dnsNames: hostNames)
                    }
                }, tags: new Dictionary<string, string>
                {
                    { "Issuer", "letsencrypt.org" }
                });

                csr = request.Csr;
            }
            catch (KeyVaultErrorException ex) when (ex.Response.StatusCode == HttpStatusCode.Conflict)
            {
                var base64Csr = await keyVaultClient.GetPendingCertificateSigningRequestAsync(Settings.Default.VaultBaseUrl, certificateName);

                csr = Convert.FromBase64String(base64Csr);
            }

            var acme = await CreateAcmeClientAsync();

            // Order の最終処理を実行し、証明書を作成
            var finalize = await acme.FinalizeOrderAsync(orderDetails.Payload.Finalize, csr);

            var certificateData = await _httpClient.GetByteArrayAsync(finalize.Payload.Certificate);

            // X509Certificate2Collection を作成
            var x509Certificates = new X509Certificate2Collection();

            x509Certificates.ImportFromPem(certificateData);

            await keyVaultClient.MergeCertificateAsync(Settings.Default.VaultBaseUrl, certificateName, x509Certificates);
        }

        private static async Task<AcmeProtocolClient> CreateAcmeClientAsync()
        {
            var account = default(AccountDetails);
            var accountKey = default(AccountKey);
            var acmeDir = default(ServiceDirectory);

            LoadState(ref account, "account.json");
            LoadState(ref accountKey, "account_key.json");
            LoadState(ref acmeDir, "directory.json");

            var acme = new AcmeProtocolClient(_acmeHttpClient, acmeDir, account, accountKey?.GenerateSigner());

            if (acmeDir == null)
            {
                acmeDir = await acme.GetDirectoryAsync();

                SaveState(acmeDir, "directory.json");

                acme.Directory = acmeDir;
            }

            await acme.GetNonceAsync();

            if (account == null || accountKey == null)
            {
                account = await acme.CreateAccountAsync(new[] { "mailto:" + Settings.Default.Contacts }, true);

                accountKey = new AccountKey
                {
                    KeyType = acme.Signer.JwsAlg,
                    KeyExport = acme.Signer.Export()
                };

                SaveState(account, "account.json");
                SaveState(accountKey, "account_key.json");

                acme.Account = account;
            }

            return acme;
        }

        private static void LoadState<T>(ref T value, string path)
        {
            var fullPath = Environment.ExpandEnvironmentVariables(@"%HOME%\.acme\" + path);

            if (!File.Exists(fullPath))
            {
                return;
            }

            var json = File.ReadAllText(fullPath);

            value = JsonConvert.DeserializeObject<T>(json);
        }

        private static void SaveState<T>(T value, string path)
        {
            var fullPath = Environment.ExpandEnvironmentVariables(@"%HOME%\.acme\" + path);
            var directoryPath = Path.GetDirectoryName(fullPath);

            if (!Directory.Exists(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            var json = JsonConvert.SerializeObject(value, Formatting.Indented);

            File.WriteAllText(fullPath, json);
        }

        private static async Task<DnsManagementClient> CreateDnsManagementClientAsync()
        {
            var tokenProvider = new AzureServiceTokenProvider();

            var accessToken = await tokenProvider.GetAccessTokenAsync("https://management.azure.com/");

            var dnsClient = new DnsManagementClient(new TokenCredentials(accessToken))
            {
                SubscriptionId = Settings.Default.SubscriptionId
            };

            return dnsClient;
        }

        private static KeyVaultClient CreateKeyVaultClient()
        {
            var tokenProvider = new AzureServiceTokenProvider();

            return new KeyVaultClient(new KeyVaultClient.AuthenticationCallback(tokenProvider.KeyVaultTokenCallback));
        }

        private static IDictionary<string, string> ParseResourceId(string resourceId)
        {
            var values = resourceId.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            return new Dictionary<string, string>
            {
                { "subscriptions", values[1] },
                { "resourceGroups", values[3] },
                { "providers", values[5] }
            };
        }
    }

    public class ChallengeResult
    {
        public string Url { get; set; }
        public string DnsRecordName { get; set; }
        public string DnsRecordValue { get; set; }
    }
}