const providerPresets = {
  siliconflow: { name:'SiliconFlow', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.siliconflow.cn/v1', imageBaseUrl:'https://api.siliconflow.cn/v1' },
  deepseek: { name:'DeepSeek', authStyle:'bearer', textProtocol:'anthropic', textBaseUrl:'https://api.deepseek.com/anthropic', imageBaseUrl:'' },
  moonshot: { name:'Moonshot / Kimi', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.moonshot.cn/v1', imageBaseUrl:'' },
  zhipu: { name:'智谱 BigModel', authStyle:'x-api-key', textProtocol:'anthropic', textBaseUrl:'https://open.bigmodel.cn/api/anthropic', imageBaseUrl:'https://open.bigmodel.cn/api/paas/v4' },
  minimax: { name:'MiniMax', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.minimaxi.com/v1', imageBaseUrl:'' },
  openai: { name:'OpenAI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.openai.com/v1', imageBaseUrl:'https://api.openai.com/v1' },
  anthropic: { name:'Anthropic', authStyle:'x-api-key', textProtocol:'anthropic', textBaseUrl:'https://api.anthropic.com', imageBaseUrl:'' },
  openrouter: { name:'OpenRouter', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://openrouter.ai/api/v1', imageBaseUrl:'' },
  together: { name:'Together AI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.together.xyz/v1', imageBaseUrl:'https://api.together.xyz/v1' },
  groq: { name:'Groq', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.groq.com/openai/v1', imageBaseUrl:'' },
  mistral: { name:'Mistral AI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.mistral.ai/v1', imageBaseUrl:'' },
  xai: { name:'xAI', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.x.ai/v1', imageBaseUrl:'https://api.x.ai/v1' },
  cerebras: { name:'Cerebras', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.cerebras.ai/v1', imageBaseUrl:'' },
  sambanova: { name:'SambaNova', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://api.sambanova.ai/v1', imageBaseUrl:'' },
  nvidia: { name:'NVIDIA NIM', authStyle:'bearer', textProtocol:'openai', textBaseUrl:'https://integrate.api.nvidia.com/v1', imageBaseUrl:'' }
};

const presetForBaseUrl = baseUrl => {
  const normalized=(baseUrl||'').toLowerCase();
  if(!normalized)return '';
  return Object.entries(providerPresets).find(([,preset])=>normalized.startsWith(preset.textBaseUrl.toLowerCase().replace('/anthropic','')))?.[0] || 'custom';
};

const visualAttachmentExtensions = new Set(['.png','.jpg','.jpeg','.webp','.gif','.bmp','.tif','.tiff','.pdf']);
const unsupportedBinaryExtensions = new Set(['.doc','.docx','.xls','.xlsx','.ppt','.pptx','.zip','.rar','.7z','.exe','.dll','.bin','.iso','.apk','.mp3','.wav','.flac','.mp4','.mov','.avi','.mkv']);
const agentDocumentExtensions = new Set(['.docx','.xlsx','.pptx']);
const visualModelPatterns = ['claude-','gpt-4o','gpt-4.1','gpt-5','gemini','vision','-vl','vl-','vlm','omni','pixtral','llava','internvl','minicpm-v','glm-4v','glm-4.5v','kimi-vl','grok-4'];

const blankProvider = () => ({
  id: '', preset: '', name: '', token: '', authStyle: 'auto',
  capabilities:{schemaVersion:2,models:{},evidencePolicy:'unknown-until-probed'},
  text: { enabled: true, protocol: 'openai', baseUrl: '', modelsText: '' },
  image: { enabled: false, protocol: 'openai-images', baseUrl: '', modelsText: '' }
});
