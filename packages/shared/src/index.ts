export {
  ApiRequestError,
  DATABASE_CHECK,
  getHealth,
  type ApiError,
  type HealthResponse,
} from './api/client';

export {
  DEFAULT_LANGUAGE,
  SUPPORTED_LANGUAGES,
  resources,
  type SupportedLanguage,
} from './i18n/index';

export { createI18n } from './i18n/setup';

export { HealthPanel } from './ui/HealthPanel';
export { LanguageSwitch } from './ui/LanguageSwitch';
