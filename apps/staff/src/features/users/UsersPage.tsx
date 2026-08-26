import {
  Alert,
  ApiRequestError,
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
  deactivateStaffAccount,
  disableStaffAccount,
  enableStaffAccount,
  findStaffAccountByEmail,
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

  const enable = useMutation({
    mutationFn: (id: string) => enableStaffAccount(id),
    onSuccess: () => {
      setNotice(t('users.enabled'));
      void queryClient.invalidateQueries({ queryKey: ACCOUNTS_QUERY_KEY });
    },
  });

  // --- The recovery path (design D4/D5) ---------------------------------------------
  //
  // `undefined` means nobody has looked yet, `null` means nobody holds the address.
  const [holder, setHolder] = useState<StaffAccountResponse | null | undefined>(undefined);

  const findHolder = useMutation({
    mutationFn: () => findStaffAccountByEmail(email),
    onSuccess: setHolder,
  });

  const retire = useMutation({
    mutationFn: (id: string) => deactivateStaffAccount(id),
    onSuccess: () => {
      setNotice(t('users.retired'));
      setHolder(undefined);
      // Clears the refusal, so the form is back to a plain invite the administrator submits
      // again themselves. Retiring and registering stay two acts they each ask for — one
      // combined button would be the role change this system does not have.
      create.reset();
      void queryClient.invalidateQueries({ queryKey: ACCOUNTS_QUERY_KEY });
    },
  });

  // Shown only for the one refusal it can help with. Any other creation failure is just an
  // error message; offering to retire an account would be a non-sequitur.
  const addressIsTaken =
    create.error instanceof ApiRequestError
    && create.error.error.code === 'auth.email_already_in_use';

  /** Drops a stale refusal so the panel cannot outlive the address it was about. */
  function resetRecovery() {
    setHolder(undefined);
    findHolder.reset();
    retire.reset();

    if (create.isError) {
      create.reset();
    }
  }

  return (
    <div className="space-y-6">
      <div>
        <h1 className="text-2xl font-semibold text-heading">{t('users.title')}</h1>
        <p className="mt-1 text-meta">{t('users.description')}</p>
      </div>

      {notice ? <Alert tone="success">{notice}</Alert> : null}
      {disable.isError ? <Alert tone="error">{describeError(disable.error)}</Alert> : null}
      {enable.isError ? <Alert tone="error">{describeError(enable.error)}</Alert> : null}

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
                onChange={(event) => {
                  setEmail(event.target.value);
                  resetRecovery();
                }}
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

            {addressIsTaken ? (
              <TakenAddressRecovery
                holder={holder}
                onFind={() => {
                  setNotice(null);
                  findHolder.mutate();
                }}
                onRetire={(id) => {
                  setNotice(null);
                  retire.mutate(id);
                }}
                finding={findHolder.isPending}
                retiring={retire.isPending}
                failure={
                  findHolder.isError
                    ? describeError(findHolder.error)
                    : retire.isError
                      ? describeError(retire.error)
                      : undefined
                }
              />
            ) : null}
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
                onEnable={() => {
                  setNotice(null);
                  enable.mutate(account.id);
                }}
                enabling={enable.isPending}
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

/**
 * The way out of "that address is already in use" (design D5).
 *
 * Two steps a person asks for separately — see who holds the address, then retire that account
 * — and never one "deactivate and invite" button. The combined version would be
 * indistinguishable in the UI from changing somebody's role, which is a thing this system
 * deliberately cannot do (`00-context.md` §5): a role is fixed when an account is created, so
 * the old account is retired and the address registered afresh as a NEW account. That is what
 * keeps the access log honest about who held which role when.
 *
 * It also has to exist at all because S11 lists staff only, so the account most likely to be in
 * the way — a patient provisioned on the portal — cannot be found by looking down the table.
 */
function TakenAddressRecovery({
  holder,
  onFind,
  onRetire,
  finding,
  retiring,
  failure,
}: {
  holder: StaffAccountResponse | null | undefined;
  onFind: () => void;
  onRetire: (id: string) => void;
  finding: boolean;
  retiring: boolean;
  failure: string | undefined;
}) {
  const { t } = useTranslation();

  return (
    <div className="space-y-3 rounded-md border border-line bg-surface-raised p-4">
      <h3 className="font-medium text-heading">{t('users.takenTitle')}</h3>
      <p className="text-sm text-meta">{t('users.takenNote')}</p>

      {failure ? <Alert tone="error">{failure}</Alert> : null}

      {holder === undefined ? (
        // `type="button"`, and so is the retire button: both live inside the invite form, and a
        // submit here would re-run the creation that just failed.
        <Button type="button" variant="secondary" size="sm" onClick={onFind} disabled={finding}>
          {t('users.findHolder')}
        </Button>
      ) : holder === null ? (
        <p className="text-sm text-meta">{t('users.holderNone')}</p>
      ) : (
        <div className="space-y-3">
          {/*
            The address, the role and the status, before anything is offered. An administrator
            about to retire an account should be reading what they are retiring — this is the
            difference between a confirmed decision and a button they clicked.
          */}
          <p className="text-sm text-body">
            {t('users.holderSummary', {
              email: holder.email,
              role: t(`roles.${holder.role}`),
              status: t(`users.status${holder.status}`),
            })}
          </p>
          <p className="text-sm text-meta">{t('users.retireNote')}</p>

          <Button
            type="button"
            variant="secondary"
            size="sm"
            onClick={() => onRetire(holder.id)}
            disabled={retiring}
          >
            {t('users.retire')}
          </Button>
        </div>
      )}
    </div>
  );
}

function AccountRow({
  account,
  onDisable,
  onEnable,
  disabling,
  enabling,
}: {
  account: StaffAccountResponse;
  onDisable: () => void;
  onEnable: () => void;
  disabling: boolean;
  enabling: boolean;
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
        {/*
          A disabled account showed no action at all until now, which made "disable" a one-way
          door that `00-context.md` §5 nonetheless described as a reversible off-switch. Restoring
          is the reverse of exactly that action: it keeps the address, unlike retiring, which
          releases it.
        */}
        {account.status === 'Disabled' ? (
          <Button variant="secondary" size="sm" onClick={onEnable} disabled={enabling}>
            {t('users.enable')}
          </Button>
        ) : (
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
