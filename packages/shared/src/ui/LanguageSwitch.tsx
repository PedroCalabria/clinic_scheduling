import { useTranslation } from 'react-i18next';
import { SUPPORTED_LANGUAGES } from '../i18n/index';

/**
 * Switches the active language between the two product languages (pt-BR / en).
 *
 * A native `<select>` with an associated label — keyboard-operable and screen-reader
 * friendly without extra work, consistent with the WCAG 2.1 AA target (03-nfr.md §6).
 */
export function LanguageSwitch() {
  const { t, i18n } = useTranslation();

  return (
    <label className="flex items-center gap-2 text-sm">
      <span>{t('common.language')}</span>
      <select
        className="rounded border border-slate-300 px-2 py-1"
        value={i18n.language}
        onChange={(event) => void i18n.changeLanguage(event.target.value)}
      >
        {SUPPORTED_LANGUAGES.map((language) => (
          <option key={language} value={language}>
            {language}
          </option>
        ))}
      </select>
    </label>
  );
}
