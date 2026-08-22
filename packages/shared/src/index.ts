export {
  ApiRequestError,
  AUTH_ERROR_PARAM,
  DATABASE_CHECK,
  changePassword,
  createAppointmentType,
  createResource,
  createResourceType,
  createSpecialty,
  createStaffAccount,
  disableStaffAccount,
  getHealth,
  getMyProfile,
  getSession,
  googleSignInUrl,
  listAppointmentTypes,
  listResourceTypes,
  listResources,
  listSpecialties,
  listStaffAccounts,
  onSessionEnded,
  renameSpecialty,
  revokeConsent,
  setCatalogEntityActive,
  signIn,
  signOut,
  updateAppointmentType,
  updateMyProfile,
  updateResource,
  updateResourceType,
  type ApiError,
  type AppointmentTypeResponse,
  type CatalogCollection,
  type ConsentResponse,
  type HealthResponse,
  type PatientProfileResponse,
  type ResourceResponse,
  type ResourceTypeResponse,
  type RoleName,
  type SessionResponse,
  type SpecialtyResponse,
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
  Dialog,
  DialogClose,
  DialogContent,
  DialogFooter,
  DialogTrigger,
  type DialogContentProps,
} from './ui/primitives/Dialog';
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
