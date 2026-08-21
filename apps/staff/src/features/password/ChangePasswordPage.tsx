import {
  Alert,
  Button,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Field,
  Input,
  SESSION_QUERY_KEY,
  changePassword,
  useApiErrorMessage,
} from '@clinic/shared';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { useNavigate } from 'react-router';

/** Mirrors the API's default minimum (Auth__MinimumPasswordLength). */
const MINIMUM_LENGTH = 12;

/**
 * Where an account still holding a password somebody else chose is sent.
 *
 * This is the screen the pipeline-level gate forces (design A6): the API refuses everything
 * except reading the session, changing the password, and signing out, so this cannot be
 * navigated past. It exists for both the bootstrapped administrator and any account an
 * administrator created in S11 — in both cases a person other than the account holder knows
 * the password.
 */
export function ChangePasswordPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const navigate = useNavigate();
  const describeError = useApiErrorMessage();

  const [currentPassword, setCurrentPassword] = useState('');
  const [newPassword, setNewPassword] = useState('');

  const submit = useMutation({
    mutationFn: () => changePassword(currentPassword, newPassword),
    onSuccess: (session) => {
      // The API issued a fresh session, so the caller is not signed out by their own change.
      queryClient.setQueryData(SESSION_QUERY_KEY, session ?? null);
      void navigate('/', { replace: true });
    },
  });

  return (
    <div className="mx-auto max-w-md px-6 py-16">
      <Card>
        <CardHeader>
          <CardTitle>{t('staff.changePasswordTitle')}</CardTitle>
          <CardDescription>{t('staff.changePasswordNote')}</CardDescription>
        </CardHeader>

        <form
          className="space-y-5"
          onSubmit={(event) => {
            event.preventDefault();
            submit.mutate();
          }}
        >
          <Field label={t('staff.currentPassword')}>
            {({ id, describedBy, invalid }) => (
              <Input
                id={id}
                type="password"
                autoComplete="current-password"
                aria-describedby={describedBy}
                aria-invalid={invalid}
                value={currentPassword}
                onChange={(event) => setCurrentPassword(event.target.value)}
                required
              />
            )}
          </Field>

          <Field
            label={t('staff.newPassword')}
            hint={t('staff.newPasswordHint', { count: MINIMUM_LENGTH })}
          >
            {({ id, describedBy, invalid }) => (
              <Input
                id={id}
                type="password"
                autoComplete="new-password"
                aria-describedby={describedBy}
                aria-invalid={invalid}
                minLength={MINIMUM_LENGTH}
                value={newPassword}
                onChange={(event) => setNewPassword(event.target.value)}
                required
              />
            )}
          </Field>

          {submit.isError ? <Alert tone="error">{describeError(submit.error)}</Alert> : null}

          <Button type="submit" className="w-full" disabled={submit.isPending}>
            {t('staff.changePassword')}
          </Button>
        </form>
      </Card>
    </div>
  );
}
