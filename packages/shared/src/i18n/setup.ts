import i18next, { type i18n } from 'i18next';
import { initReactI18next } from 'react-i18next';
import { DEFAULT_LANGUAGE, resources } from './index';

/**
 * Builds a configured i18next instance for an app.
 *
 * Shared so both frontends resolve the same key set from the same resource files — one
 * definition per string, and a single target for the `check:i18n` gate.
 *
 * `saveMissing` is off and there is no fallback prose: a missing key surfaces as the raw
 * key in the UI, which is loud on purpose. CI is the real guard (`pnpm check:i18n`).
 */
export function createI18n(): i18n {
  const instance = i18next.createInstance();

  void instance.use(initReactI18next).init({
    resources,
    lng: DEFAULT_LANGUAGE,
    fallbackLng: DEFAULT_LANGUAGE,
    interpolation: {
      // React already escapes rendered values.
      escapeValue: false,
    },
  });

  return instance;
}
