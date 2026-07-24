using Aevatar.GAgentService.Infrastructure.AgentProfiles;
using Microsoft.Extensions.Configuration;

namespace Aevatar.GAgentService.Integration.Tests;

internal static class AgentProfileIngressProofTestConfiguration
{
    private const string KeyId = "agent-profile-test-key-v1";
    private const string PrivateKeyPkcs8 =
        "MIIEvgIBADANBgkqhkiG9w0BAQEFAASCBKgwggSkAgEAAoIBAQC0WX33t11YmwMRe1jGjR6+930/fPdr5SC2+MbfGGjoAjBARpcQUDpvirEKPoY9cMKz17SIcd4jQbaXPeIgNC5aP/uBVuV5nFlzvVGA9cHL73rdXjf9BHjOAtDmdfyP4mjOwocWcojaCAA/wB/lRstsOOZwFTwxEYJH+2IhS411D6JFMRrIJN0nfBq6LxhylkfE5ykFrgdI9Nvpt1+Oxu+cULjFX0/6VFzvkSDpUyixHfi0GzAX9lHiIsd8v3wHTe/LPL+0XmwsZLtHi80iGFjGIcpzEF8c5ARmLLEGQluCYea8pKVRDIGss2BoEU8ltfEV7mKlEIJnlARyDZoaxAlBAgMBAAECggEAbr8Vr2wWEjb+J1oLJcG6w6HOc5IVjVfiQvl5hb3DjdTqNE4krYvWlnAgTx4d6NS5ex5WagMiWZwct7r0hLoGTL1FgCMQPyFXfM8goYRIQScJ163ny6NXW4o3JY4GTYTGv1CNC6fBicGoBX3BGFXkzMwUFXe0wpzx16nylGeEsgCjra1rKp/56Y8q0MPG1tOzC54yk+9Op8r9o8a3fcjMbitBWAemeZoBmrGX0CKO+AkVLsuDheNN+nPRglGG89yfvVBviqIFCKMKZ3edgRfllQGmq0SnKZdoN/BX+MlxUv3fC/K4KMdNwaHr7Qgyfim7Dmbq9a5BIM1DSP6H3JEjAQKBgQDdaDNpXFMnNKPzBtMmC3k2rFsgsqiBXbdw/A6/rwV+QjaAxoo9nRfVopPmHUScQLVbH+fQ1KUdCHCJolDkvYQ6HR97uGdnORqsW7+932NXPU/A7DlT3qZW1u1mnAoifQ94N3+EOATEoLVJdsbCjqFQCj4FQj1RizwGRXk7CX3yCQKBgQDQhxUYzZVgsuUNnsICKXhZy8WtKuIWZm7fjLY3eIy7h7OT2wz1P1Xz8g37Wi0jkIuLIdjtNePLcmeJNQWFd9H7VevsNKVef8T893AfFS1rTXzzsHJRZ9Op2Um5PsbAQc8zMlymLV0w/icSPe4cfajp3ifYR9IAphsNUyryurRLeQKBgQDH6RvymAAkuC0IdDMWeOmbaghl/6qSFDJb+9q9TKSjGdnocFvFwiARL1hnQCoBA5Q8kRRYxIfJLSOfwkVUI6JObplMtnX3B+KDmdwI7rjdvmhSg3hHuBNs+WclbOLhvRXIsCOdGI+Fkq3dhTd12B7jDDxvtx1ykUtDRltt6OYlMQKBgQCyVQO+zXpVU0i+KCpEzRBmwvTQDl+BxqJFPkJLGCZK7leuN+RSDJNGZ5h7f/ggdSpRl2W8H50rTTCsT5LkPL9wUV/NBozyTxS5Pic9/c9097THdvudEM0ccX4yFTTGEMHRR92iJCORlZj2ac4rwW9mah3rQiifc26pK5oMMY2lCQKBgCGvmkX+V30FyqwXFJVecU6zztFqAU5qokYCfdhRGTEu1EKGDBQ4hAH2OM9TnCssljju9pOf95wGsMkimS4a+ASGX8x9Bb75aJQC6JWCRjHQu33LmWlLhcoMsUkpny9pUjWniPkOkOcyhLa6E6ScBLeJGB0WSq4YrLObspESxdMS";
    private const string PublicKeySubjectPublicKeyInfo =
        "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAtFl997ddWJsDEXtYxo0evvd9P3z3a+UgtvjG3xho6AIwQEaXEFA6b4qxCj6GPXDCs9e0iHHeI0G2lz3iIDQuWj/7gVbleZxZc71RgPXBy+963V43/QR4zgLQ5nX8j+JozsKHFnKI2ggAP8Af5UbLbDjmcBU8MRGCR/tiIUuNdQ+iRTEayCTdJ3waui8YcpZHxOcpBa4HSPTb6bdfjsbvnFC4xV9P+lRc75Eg6VMosR34tBswF/ZR4iLHfL98B03vyzy/tF5sLGS7R4vNIhhYxiHKcxBfHOQEZiyxBkJbgmHmvKSlUQyBrLNgaBFPJbXxFe5ipRCCZ5QEcg2aGsQJQQIDAQAB";

    public static IConfiguration Create(
        IReadOnlyDictionary<string, string?>? additionalValues = null)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [$"{AgentProfileIngressProofOptions.SectionName}:CurrentKeyId"] = KeyId,
            [$"{AgentProfileIngressProofOptions.SectionName}:CurrentPrivateKeyPkcs8"] =
                PrivateKeyPkcs8,
            [$"{AgentProfileIngressProofOptions.SectionName}:PublicKeys:{KeyId}"] =
                PublicKeySubjectPublicKeyInfo,
        };
        if (additionalValues is not null)
        {
            foreach (var (key, value) in additionalValues)
                values[key] = value;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }
}
