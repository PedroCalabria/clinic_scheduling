export {
  ApiRequestError,
  AUTH_ERROR_PARAM,
  DATABASE_CHECK,
  changePassword,
  createStaffAccount,
  disableStaffAccount,
  getHealth,
  getMyProfile,
  getSession,
  googleSignInUrl,
  listStaffAccounts,
  onSessionEnded,
  revokeConsent,
  signIn,
  signOut,
  updateMyProfile,
  type ApiError,
  type ConsentResponse,
  type HealthResponse,
  type PatientProfileResponse,
  type RoleName,
  type SessionResponse,
  type StaffAccountResponse,
} from './api/client';

export {
  DEFAULT_LANGUAGE,
  SUPPORTED_LANGUAGES,
  resources,
  type SupportedLanguage,
} from './i18n/index';

export { createI18n } from './i18n/setup';

export {
  RequireAuth,
  SESSION_QUERY_KEY,
  SessionExpiryWatcher,
  useApiCodeMessage,
  useApiErrorMessage,
  useSession,
  useSignOut,
} from './auth/session';

export { HealthPanel } from './ui/HealthPanel';
export { LanguageSwitch } from './ui/LanguageSwitch';

export { cn } from './ui/cn';
export { Button, buttonVariants, type ButtonProps } from './ui/primitives/Button';
export { Field, Input, Label, Select } from './ui/primitives/Field';
export {
  Alert,
  Badge,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  type AlertProps,
  type BadgeProps,
} from './ui/primitives/Surfaces';
