import {
  AUTH_ERROR_PARAM,
  Alert,
  Button,
  Card,
  CardDescription,
  CardHeader,
  CardTitle,
  Field,
  Input,
  LanguageSwitch,
  SESSION_QUERY_KEY,
  signIn,
  useApiCodeMessage,
  useApiErrorMessage,
  useSession,
} from '@clinic/shared';
import { staffGoogleSignInUrl } from '../../config/signIn';
import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { useTranslation } from 'react-i18next';
import { Navigate, useSearchParams } from 'react-router';

/**
 * S0 — Staff sign-in (docs/06-ui-surfaces.md).
 *
 * One screen, two paths, deliberately labelled by WHO uses each rather than by mechanism:
 * reception and administration have accounts the clinic issued, professionals sign in with
 * the Google account the clinic registered for them. Presenting the choice as "password or
 * Google" would make every user work out which one they are.
 *
 * That labelling is also what heads off the one refusal an ordinary user can trigger here —
 * a front-desk user clicking the Google button and being told their address belongs to an
 * internal account (design A5).
 */
export function SignInPage() {
  const { t } = useTranslation();
  const queryClient = useQueryClient();
  const { data: session } = useSession();
  const [searchParams] = useSearchParams();
  const describeCode = useApiCodeMessage();
  const describeError = useApiErrorMessage();

  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');

  const redirectError = describeCode(searchParams.get(AUTH_ERROR_PARAM));

  const submit = useMutation({
    mutationFn: () => signIn(email, password),
    onSuccess: (result) => {
      // Seeded rather than refetched: the sign-in response is the same shape the session
      // endpoint returns, so the guard can decide immediately.
      queryClient.setQueryData(SESSION_QUERY_KEY, result ?? null);
    },
  });

  if (session) {
    return <Navigate to={session.mustChangePassword ? '/password' : '/'} replace />;
  }

  return (
    <div className="mx-auto max-w-md space-y-6 px-6 py-16">
      <div className="flex items-center justify-between gap-4">
        <h1 className="text-2xl font-semibold text-heading">{t('staff.signInTitle')}</h1>
        <LanguageSwitch />
      </div>

      {redirectError ? <Alert tone="error">{redirectError}</Alert> : null}

      <Card>
        <CardHeader>
          <CardTitle>{t('staff.signInInternal')}</CardTitle>
          <CardDescription>{t('staff.signInInternalNote')}</CardDescription>
        </CardHeader>

        <form
          className="space-y-5"
          onSubmit={(event) => {
            event.preventDefault();
            submit.mutate();
          }}
        >
          <Field label={t('common.email')}>
            {({ id, describedBy, invalid }) => (
              <Input
                id={id}
                type="email"
                autoComplete="username"
                aria-describedby={describedBy}
                aria-invalid={invalid}
                value={email}
                onChange={(event) => setEmail(event.target.value)}
                required
              />
            )}
          </Field>

          <Field label={t('common.password')}>
            {({ id, describedBy, invalid }) => (
              <Input
                id={id}
                type="password"
                autoComplete="current-password"
                aria-describedby={describedBy}
                aria-invalid={invalid}
                value={password}
                onChange={(event) => setPassword(event.target.value)}
                required
              />
            )}
          </Field>

          {/*
            Rendered from the code the API returned, never from server prose (Decision I).
            "Wrong password" and "no such account" arrive as the same code on purpose, so
            this message cannot leak which one happened.
          */}
          {submit.isError ? <Alert tone="error">{describeError(submit.error)}</Alert> : null}

          <Button type="submit" className="w-full" disabled={submit.isPending}>
            {t('common.signIn')}
          </Button>
        </form>
      </Card>

      <Card>
        <CardHeader>
          <CardTitle>{t('staff.signInProfessional')}</CardTitle>
          <CardDescription>{t('staff.signInProfessionalNote')}</CardDescription>
        </CardHeader>

        <Button asChild variant="secondary" className="w-full">
          <a href={staffGoogleSignInUrl()}>{t('portal.signInWithGoogle')}</a>
        </Button>
      </Card>
    </div>
  );
}
