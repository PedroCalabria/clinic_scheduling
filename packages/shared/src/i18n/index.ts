import en from './en.json';
import ptBR from './pt-BR.json';

/**
 * The two product languages (03-nfr.md §1). `pt-BR` is the default; locale resolution is
 * explicit user preference > `Accept-Language` > pt-BR.
 */
export const SUPPORTED_LANGUAGES = ['pt-BR', 'en'] as const;

export type SupportedLanguage = (typeof SUPPORTED_LANGUAGES)[number];

export const DEFAULT_LANGUAGE: SupportedLanguage = 'pt-BR';

/**
 * Translation resources, shared by both frontends so a string is defined once.
 *
 * Both locales must carry an identical key set — `pnpm check:i18n` fails the build
 * otherwise, which is how the i18n clause of the Definition of Done stays honest.
 */
export const resources = {
  'pt-BR': { translation: ptBR },
  en: { translation: en },
} as const;

export { en, ptBR };
