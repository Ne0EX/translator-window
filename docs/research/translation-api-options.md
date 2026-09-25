# Optional translation APIs for v0.2

Research date: 2026-09-25. Prices and quotas can change; verify the selected
account, region, and currency immediately before implementation.

## Scope and conclusion

The smallest useful experiment is a **text-only remote translation mode**:
keep text detection, text recognition, caption placement, and capture local;
send only recognized text and source/target language codes, retaining region IDs
locally for result association.
This can avoid loading the local Hy-MT2 translator when remote mode is selected,
but it does **not** remove MangaOCR's VRAM use or recognition warmup.

Evaluate **Azure Translator F0 first**. It has the largest documented ongoing
free allowance in this shortlist, supports the required language directions,
publishes useful small-request latency guidance, and says text translation is
not stored. Google Cloud Translation NMT is the strongest second dedicated
service. Gemini 3.5 Flash-Lite is a low-cost quality comparator, preferably on
the paid tier because its free-tier data terms are unsuitable for private reader
content. Amazon Translate is viable but costs more after its time-limited free
tier and requires an explicit service-improvement opt-out for the strongest
privacy posture.

This is an evaluation shortlist, not a provider commitment. Prove one provider
before introducing a provider abstraction.

The reader confirmed reader-supplied API keys for the first prototype on
2026-09-25. No account has been provisioned and no reader content has been sent
to a translation service during this research.

## Shortlist

| Candidate | Required directions | Price and free use | Realtime constraints | Data handling | Account and distribution notes |
|---|---|---|---|---|---|
| **Azure AI Translator, standard text** | The [language table](https://learn.microsoft.com/en-us/azure/ai-services/translator/language-support) lists English, Japanese, Korean, and Thai for cloud text translation. | [F0 includes 2 million characters/month](https://azure.microsoft.com/en-us/pricing/details/translator/). Microsoft's current pricing page is region/currency dynamic; a Microsoft pricing example uses an illustrative [US list price of $10/million characters](https://learn.microsoft.com/en-us/azure/ai-services/speech-service/speech-translation) for S1 and directs readers back to the pricing page. F0 is an ongoing SKU allowance, not an automatic credit applied to S1. | Up to 50,000 characters and 1,000 items per request. F0 allows 2 million characters/hour, recommended evenly at about 33,300/minute. Microsoft reports 150-300 ms typical latency for text under 100 characters and a 15-second maximum for standard models; actual latency varies by pair and size. See [service limits](https://learn.microsoft.com/en-us/azure/ai-services/Translator/service-limits). The text endpoint returns a complete response; it has no documented token-stream endpoint. | Microsoft says text translation [processes data at REST and does not store it](https://learn.microsoft.com/en-us/azure/ai-foundry/responsible-ai/translator/data-privacy-security?view=foundry-classic). | Requires an Azure subscription, Translator resource, endpoint, and secret key. The [quickstart](https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/quickstart/rest-api) says to keep the key out of source and public repositories. |
| **Google Cloud Translation NMT** | Google documents translation [from any supported language to any other](https://docs.cloud.google.com/translate/docs/languages), including `en`, `ja`, `ko`, and `th`. | The [first 500,000 NMT characters each month](https://cloud.google.com/products/translate/pricing) are covered by a recurring $10 credit; additional usage is $20/million source characters. Characters are Unicode code points, including whitespace and markup. A separate new-customer trial is time-limited and should not be treated as ongoing free use. | Default general-model quota is 6 million characters/minute per project and per user; v3 allows 6,000 requests/minute. Google recommends at most 5,000 code points/request for latency; Advanced rejects over 30,000. Daily content is unlimited by default. See [quotas and limits](https://docs.cloud.google.com/translate/quotas). `translateText` is whole-response; batch translation is asynchronous, not token streaming. | Google says submitted content is used only to provide the service, is [held briefly in memory, is not used to train models, and is not shared](https://docs.cloud.google.com/translate/data-usage). Advanced regional endpoints can provide a configured processing location; global endpoints cannot. | The [setup guide](https://docs.cloud.google.com/translate/docs/setup) requires a Cloud project, enabled billing, API enablement, and authenticated credentials even when usage stays within the monthly credit. Google permits integration into an application of independent value but not resale of the API itself; see the data-use page above. |
| **Amazon Translate, synchronous `TranslateText`** | AWS supports translation [between any supported languages](https://docs.aws.amazon.com/translate/latest/dg/what-is-languages.html), including `en`, `ja`, `ko`, and `th`. | [Standard text translation is $15/million characters](https://aws.amazon.com/translate/pricing/), including whitespace. New AWS customers receive 2 million characters/month for the first 12 months; this is a trial period, not ongoing free use. | Synchronous input is limited to 10,000 UTF-8 bytes. AWS publishes no fixed synchronous latency or default TPS promise; it says the service scales with operational traffic and exposes throttling/response-time metrics. See [guidelines and quotas](https://docs.aws.amazon.com/translate/latest/dg/what-is-limits.html) and [CloudWatch metrics](https://docs.aws.amazon.com/translate/latest/dg/translate-cloudwatch.html). `TranslateText` returns one complete response. | Current [AWS Service Terms, section 50.3](https://aws.amazon.com/service-terms/) allow AWS to use and store Amazon Translate content to improve its AI services unless the account uses the [AWS Organizations AI-services opt-out policy](https://docs.aws.amazon.com/organizations/latest/userguide/orgs_manage_policies_ai-opt-out_all.html). | Requires an AWS account and IAM programmatic access. AWS [recommends temporary credentials](https://docs.aws.amazon.com/translate/latest/dg/setting-up.html); requests are signed and TLS 1.2 or later is required according to [infrastructure security](https://docs.aws.amazon.com/translate/latest/dg/infrastructure-security.html). |
| **Gemini Developer API, `gemini-3.5-flash-lite`** | The model is advertised for low-cost, high-throughput text work and the pricing page explicitly includes translation, but Google does not publish a dedicated translation-pair matrix. Japanese/Korean/English to Thai therefore requires corpus qualification. | [Paid standard pricing](https://ai.google.dev/gemini-api/docs/pricing) is $0.30/million input tokens and $2.50/million output tokens. The free tier is $0 within the account's active limits, but those limits vary and are not guaranteed. | `generateContent` returns a complete response; [`streamGenerateContent` provides SSE chunks](https://ai.google.dev/api). The model's Live API is not supported, but SSE text streaming is. RPM, input TPM, and daily limits are per project and [vary by model, tier, and account](https://ai.google.dev/gemini-api/docs/rate-limits); exceeding one produces `429`. Streaming does not make a partial caption semantically safe: the app should publish only a complete, mapped result. | Under the [Gemini API Additional Terms](https://ai.google.dev/gemini-api/terms), free/unpaid prompts and responses may be used to improve products and may be reviewed by humans; sensitive, confidential, or personal information must not be submitted. Paid prompts/responses are not used to improve products, but Google retains limited safety/legal logs and may process/store data in any country. | Requests use an API key. [Billing setup](https://ai.google.dev/gemini-api/docs/billing) is required for paid use. The terms require users to be 18+ and use the service as professional/business developers; API clients offered in the EEA, Switzerland, or UK must use paid services. |

## Rough monthly cost scenarios

Assumptions: one Thai target, source-text volume of 100,000, 1 million, or
5 million characters; no taxes, currency conversion, network fees, custom
models, or image input. These are estimates, not quotes.

| Service | 100k source chars | 1M source chars | 5M source chars | Calculation note |
|---|---:|---:|---:|---|
| Azure F0 / S1 | $0 on F0 | $0 on F0 | about $50 on S1 | F0 covers at most 2M/month. The 5M scenario uses the illustrative $10/M S1 list price for all usage; do not subtract an F0 allowance from a separate paid SKU. |
| Google Cloud NMT | $0 | $10 | $90 | First 500k/month covered, then $20/M. |
| Amazon Translate, first 12 months | $0 | $0 | $45 | First 2M/month covered during the 12-month introductory period, then $15/M. |
| Amazon Translate, after 12 months | $1.50 | $15 | $75 | $15/M from the first character. |
| Gemini 3.5 Flash-Lite paid | about $0.41 | about $4.08 | about $20.40 | Unmeasured planning assumption per source character: 1.1 input tokens including amortized instruction/mapping overhead and 1.5 billable output tokens. At $0.30/M input plus $2.50/M output, cost is about $4.08/M source characters. This is not an upper bound: measure tokenization, prompt/context overhead, billable thinking tokens and retries. |

Gemini's free tier could make a small trial cost $0, but its account-specific
limits and free-tier data-use terms make it unsuitable as a privacy-preserving
default. Azure F0 and Google's monthly NMT credit still require cloud accounts
and credentials.

## Text API versus image API

Use a text API for the first proof. It transmits recognized text rather than the
captured view, keeps source artwork local, preserves the existing text-region to
caption mapping, and avoids paying for a second OCR/multimodal pass. Recognition
errors and region grouping errors still reach the translator and must be tested.

An image or multimodal API would transmit source content and artwork, expand the
consent and retention boundary, and require a new way to map results back to
text regions. It also does not remove local OCR VRAM or warmup unless local text
recognition is deliberately replaced and the whole capture-to-caption path is
requalified. That is outside the minimal v0.2 proof.

## Minimal evaluation plan

1. Add no provider framework yet. Build one isolated Azure Translator proof with
   reader-supplied credentials, explicit opt-in, and a visible statement that
   recognized text leaves the computer. Do not embed a shared vendor key in the
   desktop binary.
2. Keep an ordered `{region_id, text}` mapping locally. For Azure, send the
   documented array of `{Text: ...}` objects and validate/map the corresponding
   result array before publishing captions; arbitrary region-ID fields are not
   part of the [Translate request contract](https://learn.microsoft.com/en-us/azure/ai-services/translator/text-translation/reference/v3/translate).
   Preserve cancellation, stale-view rejection and placement; include provider,
   source/target language and any context policy in translation cache identity.
3. Test Japanese, Korean, and English to Thai using the existing protected
   meaning cases plus Korean/English cases. Record meaning preservation,
   unmapped/missing results, p50/p95 first-response latency, throttling and retry
   behavior, and actual billed characters.
4. Compare Google NMT only if Azure fails quality, setup, quota, or latency gates.
   Compare paid Gemini Flash-Lite only if dedicated translation quality is not
   adequate. Evaluate Amazon Translate after its privacy opt-out and account
   setup are acceptable.
5. Keep local processing as the default until the reader explicitly selects a
   remote provider. A remote session must never be described as local processing.
