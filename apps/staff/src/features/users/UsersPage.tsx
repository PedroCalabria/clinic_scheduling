import {
  Alert,
  Badge,
  Button,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Field,
  Input,
  Select,
  Table,
  TableCell,
  TableHead,
  TableHeaderCell,
  TableRow,
  createStaffAccount,
  disableStaffAccount,
  listStaffAccounts,
  useApiErrorMessage,
  type RoleName,
  type StaffAccountResponse,
} from '@clinic/shared';
import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';

const ACCOUNTS_QUERY_KEY = ['staff-accounts'] as const;

/** Roles an administrator can create here. Patients are not among them, by design. */
const CREATABLE_ROLES: readonly RoleName[] = ['FrontDesk', 'Administrator', 'Professional'];

/**
 * S11 — Users (docs/06-ui-surfaces.md).
 *
 * Load-bearing in this change rather than a stub: the invite-first rule makes this the only
 * way a professional identity comes into existence (design A5), which is why the form
 * explains that a professional is registered by the address they will sign in with.
 *
 * Utilitarian on purpose (Z2) — a table and a form, no ornament.
 */
export function UsersPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const describeError = useApiErrorMessage();

  const { data: accounts, isPending, isError, error } = useQuery({
    queryKey: ACCOUNTS_QUERY_KEY,
    queryFn: listStaffAccounts,
    retry: false,
  });

  const [email, setEmail] = useState('');
  const [role, setRole] = useState<RoleName>('FrontDesk');
  const [password, setPassword] = useState('');
  const [notice, setNotice] = useState<string | null>(null);

  const needsPassword = role !== 'Professional';

  const create = useMutation({
    mutationFn: () =>
      createStaffAccount({ email, role, password: needsPassword ? password : undefined }),
    onSuccess: () => {
      setEmail('');
      setPassword('');
      setNotice(t('users.created'));
      void queryClient.invalidateQueries({ queryKey: ACCOUNTS_QUERY_KEY });
    },
  });

  const disable = useMutation({
    mutationFn: (id: string) => disableStaffAccount(id),
    onSuccess: () => {
      setNotice(t('users.disabled'));
      void queryClient.invalidateQueries({ queryKey: ACCOUNTS_QUERY_KEY });
    },
  });

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-heading">{t('users.title')}</h1>
        <p className="mt-1 text-meta">{t('users.description')}</p>
      </div>

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {disable.isError ? <Alert tone="error">{describeError(disable.error)}</Alert> : null}

      <Card>
        <CardHeader>
          <CardTitle>{t('users.addTitle')}</CardTitle>
          <CardDescription>{t('users.addNote')}</CardDescription>
        </CardHeader>

        <form
          className="grid gap-5 md:grid-cols-3"
          onSubmit={(event) => {
            event.preventDefault();
            setNotice(null);
            create.mutate();
          }}
        >
          <Field label={t('common.email')}>
            {({ id, describedBy, invalid }) => (
              <Input
                id={id}
                type="email"
                aria-describedby={describedBy}
                aria-invalid={invalid}
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                required
              />
            )}
          </Field>

          <Field label={t('users.role')}>
            {({ id, describedBy }) => (
              <Select
                id={id}
                aria-describedby={describedBy}
                value={role}
                onChange={(event) => setRole(event.target.value as RoleName)}
              >
                {CREATABLE_ROLES.map((option) => (
                  <option key={option} value={option}>
                    {t(`roles.${option}`)}
                  </option>
                ))}
              </Select>
            )}
          </Field>

          {/*
            The password field disappears for a professional rather than being disabled: a
            professional account has no password at all, and an empty box would suggest one
            was merely optional.
          */}
          {needsPassword ? (
            <Field label={t('common.password')}>
              {({ id, describedBy, invalid }) => (
                <Input
                  id={id}
                  type="password"
                  autoComplete="new-password"
                  aria-describedby={describedBy}
                  aria-invalid={invalid}
                  value={password}
                  onChange={(event) => setPassword(event.target.value)}
                  required
                />
              )}
            </Field>
          ) : (
            <p className="self-end text-sm text-meta">{t('users.professionalPasswordNote')}</p>
          )}

          <div className="md:col-span-3 space-y-4">
            {create.isError ? <Alert tone="error">{describeError(create.error)}</Alert> : null}

            <Button type="submit" disabled={create.isPending}>
              {t('users.create')}
            </Button>
          </div>
        </form>
      </Card>

      {isPending ? (
        <p role="status" className="text-meta">
          {t('common.loading')}
        </p>
      ) : isError ? (
        <Alert tone="error">{describeError(error)}</Alert>
      ) : accounts && accounts.length > 0 ? (
        <Table>
          <TableHead>
            <TableRow>
              <TableHeaderCell>{t('users.columnEmail')}</TableHeaderCell>
              <TableHeaderCell>{t('users.columnRole')}</TableHeaderCell>
              <TableHeaderCell>{t('users.columnStatus')}</TableHeaderCell>
              <TableHeaderCell>{t('users.columnSignsInWith')}</TableHeaderCell>
              <TableHeaderCell>{t('users.columnActions')}</TableHeaderCell>
            </TableRow>
          </TableHead>
          <tbody>
            {accounts.map((account) => (
              <AccountRow
                key={account.id}
                account={account}
                onDisable={() => {
                  setNotice(null);
                  disable.mutate(account.id);
                }}
                disabling={disable.isPending}
              />
            ))}
          </tbody>
        </Table>
      ) : (
        <p className="text-meta">{t('users.empty')}</p>
      )}
    </div>
  );
}

function AccountRow({
  account,
  onDisable,
  disabling,
}: {
  account: StaffAccountResponse;
  onDisable: () => void;
  disabling: boolean;
}) {
  const { t } = useTranslation();

  return (
    <TableRow>
      <TableCell className="font-medium">{account.email}</TableCell>
      <TableCell>{t(`roles.${account.role}`)}</TableCell>
      <TableCell>
        <Badge tone={statusTone(account.status)}>{t(`users.status${account.status}`)}</Badge>
        {account.awaitsClaim ? (
          <span className="ml-2 text-xs text-meta">{t('users.awaitsClaim')}</span>
        ) : null}
      </TableCell>
      <TableCell>
        {account.authProvider === 'Google' ? t('users.providerGoogle') : t('users.providerInternal')}
      </TableCell>
      <TableCell>
        {account.status === 'Disabled' ? null : (
          <Button variant="secondary" size="sm" onClick={onDisable} disabled={disabling}>
            {t('users.disable')}
          </Button>
        )}
      </TableCell>
    </TableRow>
  );
}

function statusTone(status: string): 'active' | 'pending' | 'off' | 'neutral' {
  switch (status) {
    case 'Active':
      return 'active';
    case 'PendingClaim':
      return 'pending';
    case 'Disabled':
    case 'Locked':
      return 'off';
    default:
      return 'neutral';
  }
}
