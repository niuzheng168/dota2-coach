import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { AzureCliCredential, ChainedTokenCredential, DefaultAzureCredential } from '@azure/identity';

const __dirname = path.dirname(fileURLToPath(import.meta.url));
try {
  process.loadEnvFile?.(path.resolve(__dirname, '../.env'));
} catch {
  // `.env` is optional when environment variables are provided directly
}

const COGNITIVE_SCOPE = 'https://cognitiveservices.azure.com/.default';

export interface FoundryConfig {
  accountName: string;
  region: string;
  resourceId: string;
  openAiHost: string;
  aiServicesHost: string;
  cognitiveHost: string;
  ttsHost: string;
  deployments: {
    cascadeAsrStreaming: string;
    cascadeAsrBatch: string;
    cascadeLlm: string;
    cascadeLlmFast: string;
    visionLlm: string;
    gptRealtime: string;
    gptRealtimeMini: string;
    gptLive: string;
  };
}

const ACCOUNT_NAME = process.env.AZURE_FOUNDRY_ACCOUNT || 'your-foundry-account';
const SPEECH_REGION = process.env.AZURE_SPEECH_REGION || 'eastus2';

export const DEFAULT_FOUNDRY_CONFIG: FoundryConfig = {
  accountName: ACCOUNT_NAME,
  region: SPEECH_REGION,
  resourceId:
    process.env.AZURE_FOUNDRY_RESOURCE_ID ||
    `/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/your-rg/providers/Microsoft.CognitiveServices/accounts/${ACCOUNT_NAME}`,
  openAiHost: process.env.AZURE_OPENAI_HOST || `${ACCOUNT_NAME}.openai.azure.com`,
  aiServicesHost: process.env.AZURE_AI_SERVICES_HOST || `${ACCOUNT_NAME}.services.ai.azure.com`,
  cognitiveHost: process.env.AZURE_COGNITIVE_HOST || `${ACCOUNT_NAME}.cognitiveservices.azure.com`,
  ttsHost: process.env.AZURE_TTS_HOST || `${SPEECH_REGION}.tts.speech.microsoft.com`,
  deployments: {
    cascadeAsrStreaming:
      process.env.DEPLOYMENT_MAI_STREAMING || 'mai-transcribe-2-streaming-global-standard',
    cascadeAsrBatch:
      process.env.DEPLOYMENT_MAI_BATCH || 'mai-transcribe-1-global-standard',
    cascadeLlm:
      process.env.DEPLOYMENT_GPT_TEXT || 'gpt-6-luna-datazone-standard',
    cascadeLlmFast:
      process.env.DEPLOYMENT_GPT_TEXT_FAST || 'gpt-6-luna-datazone-standard',
    visionLlm:
      process.env.DEPLOYMENT_GPT_VISION || 'gpt-6-luna-datazone-standard',
    gptRealtime:
      process.env.DEPLOYMENT_GPT_REALTIME || 'gpt-realtime-2.1-global-standard',
    gptRealtimeMini:
      process.env.DEPLOYMENT_GPT_REALTIME_MINI || 'gpt-realtime-2.1-mini-global-standard',
    gptLive:
      process.env.DEPLOYMENT_GPT_LIVE || 'gpt-live-1-test',
  },
};

class AzureTokenManager {
  private credential = new ChainedTokenCredential(
    new AzureCliCredential(),
    new DefaultAzureCredential(),
  );
  private cachedToken: string | null = null;
  private expiresAtMs = 0;
  private pendingFetch: Promise<string> | null = null;

  async getBearerToken(): Promise<string> {
    if (process.env.AZURE_API_KEY) {
      return process.env.AZURE_API_KEY;
    }
    const now = Date.now();
    if (this.cachedToken && this.expiresAtMs - now > 120_000) {
      return this.cachedToken;
    }
    if (!this.pendingFetch) {
      this.pendingFetch = (async () => {
        try {
          const tokenResp = await this.credential.getToken(COGNITIVE_SCOPE);
          if (!tokenResp?.token) {
            throw new Error('Failed to obtain Entra ID token for Cognitive Services');
          }
          this.cachedToken = tokenResp.token;
          this.expiresAtMs = tokenResp.expiresOnTimestamp;
          return tokenResp.token;
        } finally {
          this.pendingFetch = null;
        }
      })();
    }
    return this.pendingFetch;
  }

  async getAuthHeaders(): Promise<Record<string, string>> {
    if (process.env.AZURE_API_KEY) {
      return { 'api-key': process.env.AZURE_API_KEY };
    }
    const token = await this.getBearerToken();
    return { Authorization: `Bearer ${token}` };
  }

  /**
   * Azure Speech Services (TTS / STT regional endpoints like eastus2.tts.speech.microsoft.com)
   * use `aad#<resourceId>#<accessToken>` when authenticating with Entra ID tokens on custom-subdomain AIServices resources.
   */
  async getSpeechAuthHeaders(config: FoundryConfig = DEFAULT_FOUNDRY_CONFIG): Promise<Record<string, string>> {
    if (process.env.AZURE_API_KEY) {
      return { 'Ocp-Apim-Subscription-Key': process.env.AZURE_API_KEY };
    }
    const token = await this.getBearerToken();
    return {
      Authorization: `Bearer aad#${config.resourceId}#${token}`,
    };
  }
}

export const tokenManager = new AzureTokenManager();
