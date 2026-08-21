/* @ds-bundle: {"format":4,"namespace":"ConsultRioDesignSystem_8bb054","components":[{"name":"Button","sourcePath":"components/core/Button.jsx"},{"name":"Card","sourcePath":"components/core/Card.jsx"},{"name":"Divider","sourcePath":"components/core/Divider.jsx"},{"name":"Icon","sourcePath":"components/core/Icon.jsx"},{"name":"MetaText","sourcePath":"components/core/MetaText.jsx"},{"name":"DataTable","sourcePath":"components/data/DataTable.jsx"},{"name":"Tooltip","sourcePath":"components/feedback/Tooltip.jsx"},{"name":"Input","sourcePath":"components/forms/Input.jsx"},{"name":"ConflictBanner","sourcePath":"components/scheduling/ConflictBanner.jsx"},{"name":"SlotChip","sourcePath":"components/scheduling/SlotChip.jsx"},{"name":"StatusTag","sourcePath":"components/scheduling/StatusTag.jsx"}],"sourceHashes":{"components/core/Button.jsx":"c3880a642f4c","components/core/Card.jsx":"35cbd18ed0c3","components/core/Divider.jsx":"da965cdad5ea","components/core/Icon.jsx":"72e7db1d14ef","components/core/MetaText.jsx":"5c757e8bdd5e","components/data/DataTable.jsx":"7c4bf139ab54","components/feedback/Tooltip.jsx":"77403ed2d462","components/forms/Input.jsx":"e0c198f9f8e3","components/scheduling/ConflictBanner.jsx":"44a1a07509da","components/scheduling/SlotChip.jsx":"37455c4cab9c","components/scheduling/StatusTag.jsx":"50261c6b41c1","ui_kits/patient-portal/BookingConfirmed.jsx":"7e065ff61300","ui_kits/patient-portal/BookingForm.jsx":"1171ddd290b5","ui_kits/patient-portal/PortalChrome.jsx":"e6a69730f641","ui_kits/patient-portal/SlotPicker.jsx":"c79d37fe3850","ui_kits/staff-console/ConflictRail.jsx":"6d50279506ee","ui_kits/staff-console/ConsoleShell.jsx":"aa4ece44740a","ui_kits/staff-console/DayTable.jsx":"a5ee4d3354bc"},"inlinedExternals":[],"unexposedExports":[]} */

(() => {

const __ds_ns = (window.ConsultRioDesignSystem_8bb054 = window.ConsultRioDesignSystem_8bb054 || {});

const __ds_scope = {};

(__ds_ns.__errors = __ds_ns.__errors || []);

// components/core/Card.jsx
try { (() => {
function Card({
  padding = 'lg',
  as: Tag = 'div',
  children,
  style,
  onClick
}) {
  return /*#__PURE__*/React.createElement(Tag, {
    onClick: onClick,
    style: {
      background: 'var(--surface-card)',
      color: 'var(--text-body)',
      borderRadius: 'var(--radius-md)',
      padding: `var(--space-${padding})`,
      border: 0,
      boxShadow: 'none',
      ...style
    }
  }, children);
}
Object.assign(__ds_scope, { Card });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Card.jsx", error: String((e && e.message) || e) }); }

// components/core/Divider.jsx
try { (() => {
function Divider({
  vertical = false,
  spacing = 0,
  style
}) {
  return /*#__PURE__*/React.createElement("div", {
    role: "separator",
    style: vertical ? {
      width: 1,
      alignSelf: 'stretch',
      background: 'var(--border)',
      margin: `0 ${spacing}px`,
      ...style
    } : {
      height: 1,
      width: '100%',
      background: 'var(--border)',
      margin: `${spacing}px 0`,
      ...style
    }
  });
}
Object.assign(__ds_scope, { Divider });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Divider.jsx", error: String((e && e.message) || e) }); }

// components/core/Icon.jsx
try { (() => {
/* Lucide icon wrapper. Lucide is loaded from CDN by the host page:
   <script src="https://unpkg.com/lucide@0.544.0/dist/umd/lucide.js"></script>
   Renders from lucide's own icon data — no hand-drawn paths. */
const NS = 'http://www.w3.org/2000/svg';
function pascal(name) {
  return String(name).split(/[-_ ]/).filter(Boolean).map(p => p[0].toUpperCase() + p.slice(1)).join('');
}
function build(node, size, strokeWidth) {
  const svg = document.createElementNS(NS, 'svg');
  svg.setAttribute('viewBox', '0 0 24 24');
  svg.setAttribute('width', size);
  svg.setAttribute('height', size);
  svg.setAttribute('fill', 'none');
  svg.setAttribute('stroke', 'currentColor');
  svg.setAttribute('stroke-width', strokeWidth);
  svg.setAttribute('stroke-linecap', 'round');
  svg.setAttribute('stroke-linejoin', 'round');
  (node || []).forEach(([tag, attrs]) => {
    const child = document.createElementNS(NS, tag);
    Object.entries(attrs || {}).forEach(([k, v]) => child.setAttribute(k, v));
    svg.appendChild(child);
  });
  return svg;
}
function Icon({
  name,
  size = 16,
  strokeWidth = 1.75,
  color,
  style,
  className
}) {
  const ref = React.useRef(null);
  React.useEffect(() => {
    let cancelled = false;
    const draw = () => {
      const el = ref.current;
      const lib = window.lucide && window.lucide.icons;
      if (!el || !lib) return false;
      const node = lib[pascal(name)] || lib[name];
      if (!node) return false;
      el.innerHTML = '';
      el.appendChild(build(node, size, strokeWidth));
      return true;
    };
    if (draw()) return;
    const t = setInterval(() => {
      if (!cancelled && draw()) clearInterval(t);
    }, 120);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, [name, size, strokeWidth]);
  return /*#__PURE__*/React.createElement("span", {
    ref: ref,
    "aria-hidden": "true",
    className: className,
    style: {
      display: 'inline-flex',
      width: size,
      height: size,
      color: color || 'currentColor',
      flex: '0 0 auto',
      ...style
    }
  });
}
Object.assign(__ds_scope, { Icon });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Icon.jsx", error: String((e && e.message) || e) }); }

// components/core/Button.jsx
try { (() => {
const FILL = {
  primary: {
    background: 'var(--color-primary)',
    color: 'var(--text-on-primary)',
    hover: 'var(--color-primary-strong)'
  },
  secondary: {
    background: 'var(--surface-raised)',
    color: 'var(--color-primary)',
    hover: 'var(--color-primary-subtle)'
  },
  danger: {
    background: 'var(--color-error)',
    color: 'var(--text-on-primary)',
    hover: '#8B3529'
  }
};
const PAD = {
  md: '12px 20px',
  sm: '8px 12px'
};
function Button({
  variant = 'primary',
  size = 'md',
  icon,
  iconEnd,
  disabled,
  fullWidth,
  type = 'button',
  onClick,
  children,
  style
}) {
  const [hover, setHover] = React.useState(false);
  const v = FILL[variant] || FILL.primary;
  return /*#__PURE__*/React.createElement("button", {
    type: type,
    disabled: disabled,
    onClick: onClick,
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center',
      gap: 'var(--space-sm)',
      minHeight: size === 'md' ? 'var(--touch-min)' : '32px',
      width: fullWidth ? '100%' : 'auto',
      padding: PAD[size] || PAD.md,
      background: disabled ? 'var(--surface-raised)' : hover ? v.hover : v.background,
      color: disabled ? 'var(--color-secondary)' : v.color,
      border: 0,
      borderRadius: 'var(--radius-sm)',
      fontFamily: 'var(--font-sans)',
      fontSize: 'var(--text-label-caps-size)',
      fontWeight: 600,
      lineHeight: 'var(--text-label-caps-lh)',
      letterSpacing: 'var(--text-label-caps-ls)',
      textTransform: 'uppercase',
      cursor: disabled ? 'not-allowed' : 'pointer',
      opacity: disabled ? 0.6 : 1,
      transition: 'background var(--motion-fast) var(--motion-ease)',
      ...style
    }
  }, icon ? /*#__PURE__*/React.createElement(__ds_scope.Icon, {
    name: icon,
    size: 14
  }) : null, children, iconEnd ? /*#__PURE__*/React.createElement(__ds_scope.Icon, {
    name: iconEnd,
    size: 14
  }) : null);
}
Object.assign(__ds_scope, { Button });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/Button.jsx", error: String((e && e.message) || e) }); }

// components/core/MetaText.jsx
try { (() => {
function MetaText({
  mono = false,
  children,
  style
}) {
  return /*#__PURE__*/React.createElement("span", {
    className: mono ? 'type-data-md' : 'type-body-sm',
    style: {
      color: 'var(--text-meta)',
      ...style
    }
  }, children);
}
Object.assign(__ds_scope, { MetaText });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/core/MetaText.jsx", error: String((e && e.message) || e) }); }

// components/data/DataTable.jsx
try { (() => {
function DataTable({
  columns = [],
  rows = [],
  onRowClick,
  emptyLabel = 'Nenhum registro',
  style
}) {
  return /*#__PURE__*/React.createElement("table", {
    style: {
      width: '100%',
      borderCollapse: 'collapse',
      background: 'transparent',
      ...style
    }
  }, /*#__PURE__*/React.createElement("thead", null, /*#__PURE__*/React.createElement("tr", null, columns.map(c => /*#__PURE__*/React.createElement("th", {
    key: c.key,
    className: "type-label-caps",
    style: {
      textAlign: c.align || 'left',
      color: 'var(--text-meta)',
      padding: '8px 12px',
      borderBottom: '1px solid var(--border)',
      whiteSpace: 'nowrap',
      width: c.width
    }
  }, c.header)))), /*#__PURE__*/React.createElement("tbody", null, rows.length === 0 ? /*#__PURE__*/React.createElement("tr", null, /*#__PURE__*/React.createElement("td", {
    colSpan: columns.length,
    className: "type-body-sm",
    style: {
      padding: 'var(--space-lg)',
      color: 'var(--text-meta)'
    }
  }, emptyLabel)) : rows.map((row, i) => /*#__PURE__*/React.createElement("tr", {
    key: row.id || i,
    onClick: () => onRowClick && onRowClick(row),
    style: {
      cursor: onRowClick ? 'pointer' : 'default'
    }
  }, columns.map(c => /*#__PURE__*/React.createElement("td", {
    key: c.key,
    className: c.mono ? 'type-data-md' : 'type-body-sm',
    style: {
      textAlign: c.align || 'left',
      color: c.meta ? 'var(--text-meta)' : 'var(--text-body)',
      padding: '8px 12px',
      borderBottom: '1px solid var(--border)',
      verticalAlign: 'middle'
    }
  }, c.render ? c.render(row) : row[c.key]))))));
}
Object.assign(__ds_scope, { DataTable });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/data/DataTable.jsx", error: String((e && e.message) || e) }); }

// components/feedback/Tooltip.jsx
try { (() => {
function Tooltip({
  content,
  placement = 'top',
  children,
  style
}) {
  const [open, setOpen] = React.useState(false);
  const timer = React.useRef(null);
  const show = () => {
    timer.current = setTimeout(() => setOpen(true), 150);
  };
  const hide = () => {
    clearTimeout(timer.current);
    setOpen(false);
  };
  React.useEffect(() => () => clearTimeout(timer.current), []);
  const pos = placement === 'bottom' ? {
    top: 'calc(100% + 6px)',
    left: '50%',
    transform: 'translateX(-50%)'
  } : {
    bottom: 'calc(100% + 6px)',
    left: '50%',
    transform: 'translateX(-50%)'
  };
  return /*#__PURE__*/React.createElement("span", {
    style: {
      position: 'relative',
      display: 'inline-flex',
      ...style
    },
    onMouseEnter: show,
    onMouseLeave: hide,
    onFocus: show,
    onBlur: hide
  }, children, open ? /*#__PURE__*/React.createElement("span", {
    role: "tooltip",
    className: "type-body-sm",
    style: {
      position: 'absolute',
      ...pos,
      zIndex: 20,
      background: 'var(--surface-inverse)',
      color: 'var(--text-on-inverse)',
      borderRadius: 'var(--radius-sm)',
      padding: 'var(--space-sm)',
      boxShadow: 'var(--shadow-float)',
      whiteSpace: 'nowrap',
      pointerEvents: 'none'
    }
  }, content) : null);
}
Object.assign(__ds_scope, { Tooltip });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/feedback/Tooltip.jsx", error: String((e && e.message) || e) }); }

// components/forms/Input.jsx
try { (() => {
function Input({
  label,
  value,
  onChange,
  placeholder,
  type = 'text',
  error,
  hint,
  id,
  disabled,
  required,
  fullWidth = true,
  style
}) {
  const [focus, setFocus] = React.useState(false);
  const inputId = id || `in-${String(label || 'field').replace(/\s+/g, '-').toLowerCase()}`;
  const invalid = Boolean(error);
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-xs)',
      width: fullWidth ? '100%' : 'auto',
      ...style
    }
  }, /*#__PURE__*/React.createElement("label", {
    htmlFor: inputId,
    className: "type-label-md",
    style: {
      color: 'var(--text-body)'
    }
  }, label, required ? /*#__PURE__*/React.createElement("span", {
    style: {
      color: 'var(--color-error)'
    }
  }, " *") : null), /*#__PURE__*/React.createElement("input", {
    id: inputId,
    type: type,
    value: value,
    placeholder: placeholder,
    disabled: disabled,
    "aria-invalid": invalid,
    onChange: e => onChange && onChange(e.target.value),
    onFocus: () => setFocus(true),
    onBlur: () => setFocus(false),
    style: {
      minHeight: 'var(--touch-min)',
      padding: '8px 12px',
      background: 'var(--surface)',
      color: invalid ? 'var(--text-error)' : 'var(--text-body)',
      border: `1px solid ${invalid ? 'var(--color-error)' : focus ? 'var(--border-active)' : 'var(--border)'}`,
      borderRadius: 'var(--radius-sm)',
      fontFamily: 'var(--font-sans)',
      fontSize: 'var(--text-body-md-size)',
      lineHeight: 'var(--text-body-md-lh)',
      outline: focus ? '2px solid var(--focus-ring)' : 'none',
      outlineOffset: 2
    }
  }), invalid ? /*#__PURE__*/React.createElement("span", {
    className: "type-body-sm",
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 'var(--space-xs)',
      color: 'var(--text-error)'
    }
  }, /*#__PURE__*/React.createElement(__ds_scope.Icon, {
    name: "alert-circle",
    size: 14
  }), error) : hint ? /*#__PURE__*/React.createElement("span", {
    className: "type-body-sm",
    style: {
      color: 'var(--text-meta)'
    }
  }, hint) : null);
}
Object.assign(__ds_scope, { Input });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/forms/Input.jsx", error: String((e && e.message) || e) }); }

// components/scheduling/ConflictBanner.jsx
try { (() => {
function ConflictBanner({
  title = 'Conflito de agenda',
  detail,
  appointment,
  external,
  onReschedule,
  onCancel,
  style
}) {
  return /*#__PURE__*/React.createElement("div", {
    role: "alert",
    style: {
      display: 'flex',
      gap: 'var(--space-md)',
      alignItems: 'flex-start',
      background: 'var(--color-tertiary-subtle)',
      color: 'var(--text-body)',
      borderRadius: 'var(--radius-md)',
      padding: 'var(--space-md)',
      ...style
    }
  }, /*#__PURE__*/React.createElement(__ds_scope.Icon, {
    name: "alert-triangle",
    size: 18,
    style: {
      color: 'var(--color-tertiary)',
      marginTop: 2
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-sm)',
      flex: 1
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-label-md"
  }, title), detail ? /*#__PURE__*/React.createElement("span", {
    className: "type-body-sm"
  }, detail) : null, appointment || external ? /*#__PURE__*/React.createElement("div", {
    className: "type-data-md",
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 2,
      color: 'var(--text-body)'
    }
  }, appointment ? /*#__PURE__*/React.createElement("span", null, "consulta \xB7 ", appointment) : null, external ? /*#__PURE__*/React.createElement("span", null, "bloqueio externo \xB7 ", external) : null) : null, onReschedule || onCancel ? /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 'var(--space-sm)',
      marginTop: 'var(--space-xs)'
    }
  }, onReschedule ? /*#__PURE__*/React.createElement(__ds_scope.Button, {
    size: "sm",
    variant: "primary",
    icon: "calendar-clock",
    onClick: onReschedule
  }, "Reagendar") : null, onCancel ? /*#__PURE__*/React.createElement(__ds_scope.Button, {
    size: "sm",
    variant: "danger",
    icon: "x",
    onClick: onCancel
  }, "Cancelar") : null) : null));
}
Object.assign(__ds_scope, { ConflictBanner });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/scheduling/ConflictBanner.jsx", error: String((e && e.message) || e) }); }

// components/scheduling/SlotChip.jsx
try { (() => {
function SlotChip({
  time,
  state = 'free',
  label,
  selected,
  onSelect,
  style
}) {
  const [hover, setHover] = React.useState(false);
  const free = state === 'free';
  const bg = !free ? 'transparent' : selected ? 'var(--color-primary)' : hover ? 'var(--color-primary-subtle)' : 'var(--surface-slot-free)';
  return /*#__PURE__*/React.createElement("button", {
    type: "button",
    disabled: !free,
    "aria-pressed": free ? Boolean(selected) : undefined,
    onClick: () => free && onSelect && onSelect(time),
    onMouseEnter: () => setHover(true),
    onMouseLeave: () => setHover(false),
    style: {
      display: 'flex',
      flexDirection: 'column',
      alignItems: 'flex-start',
      gap: 2,
      minHeight: 'var(--touch-min)',
      minWidth: 92,
      padding: 'var(--space-sm)',
      background: bg,
      color: !free ? 'var(--text-meta)' : selected ? 'var(--text-on-primary)' : 'var(--text-slot-free)',
      border: free ? 0 : '1px dashed var(--border)',
      borderRadius: 'var(--radius-sm)',
      cursor: free ? 'pointer' : 'default',
      transition: 'background var(--motion-fast) var(--motion-ease)',
      ...style
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-data-lg"
  }, time), /*#__PURE__*/React.createElement("span", {
    className: "type-label-caps",
    style: {
      opacity: free ? 1 : 0.9
    }
  }, label || (free ? 'Reservar' : 'Ocupado')));
}
Object.assign(__ds_scope, { SlotChip });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/scheduling/SlotChip.jsx", error: String((e && e.message) || e) }); }

// components/scheduling/StatusTag.jsx
try { (() => {
const STATES = {
  scheduled: {
    label: 'Agendado',
    icon: 'calendar-check',
    bg: 'var(--status-scheduled)',
    fg: 'var(--text-on-primary)'
  },
  completed: {
    label: 'Concluído',
    icon: 'check',
    bg: 'transparent',
    fg: 'var(--status-completed)',
    border: true
  },
  noshow: {
    label: 'Não veio',
    icon: 'user-x',
    bg: 'var(--status-noshow)',
    fg: 'var(--text-on-primary)'
  },
  cancelled: {
    label: 'Cancelado',
    icon: 'ban',
    bg: 'transparent',
    fg: 'var(--status-cancelled)',
    border: true,
    strike: true
  },
  rescheduled: {
    label: 'Reagendado',
    icon: 'arrow-right',
    bg: 'transparent',
    fg: 'var(--status-rescheduled)',
    border: true
  },
  conflict: {
    label: 'Conflito',
    icon: 'alert-triangle',
    bg: 'var(--status-conflict)',
    fg: 'var(--text-body)'
  }
};
function StatusTag({
  status = 'scheduled',
  label,
  style
}) {
  const s = STATES[status] || STATES.scheduled;
  return /*#__PURE__*/React.createElement("span", {
    className: "type-data-sm",
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 'var(--space-xs)',
      padding: '4px 6px',
      background: s.bg,
      color: s.fg,
      border: s.border ? '1px solid var(--border)' : 0,
      borderRadius: 'var(--radius-sm)',
      textTransform: 'uppercase',
      textDecoration: s.strike ? 'line-through' : 'none',
      whiteSpace: 'nowrap',
      ...style
    }
  }, /*#__PURE__*/React.createElement(__ds_scope.Icon, {
    name: s.icon,
    size: 12
  }), label || s.label);
}
Object.assign(__ds_scope, { StatusTag });
})(); } catch (e) { __ds_ns.__errors.push({ path: "components/scheduling/StatusTag.jsx", error: String((e && e.message) || e) }); }

// ui_kits/patient-portal/BookingConfirmed.jsx
try { (() => {
const {
  Card,
  Button,
  MetaText,
  Divider,
  StatusTag,
  Icon
} = window.ConsultRioDesignSystem_8bb054;
function BookingConfirmed({
  slot,
  onRestart
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-lg)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-md)'
    }
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "calendar-check",
    size: 28,
    color: "var(--color-primary)"
  }), /*#__PURE__*/React.createElement("h1", {
    className: "type-headline-lg",
    style: {
      margin: 0
    }
  }, "Hor\xE1rio reservado")), /*#__PURE__*/React.createElement("p", {
    className: "type-body-lg",
    style: {
      maxWidth: 'var(--measure-portal)',
      margin: 0
    }
  }, "Enviamos a confirma\xE7\xE3o por SMS. Chegue 10 minutos antes com um documento com foto."), /*#__PURE__*/React.createElement(Card, {
    style: {
      maxWidth: 480
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      alignItems: 'center'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-headline-md"
  }, "Quinta, 14 de agosto"), /*#__PURE__*/React.createElement(StatusTag, {
    status: "scheduled"
  })), /*#__PURE__*/React.createElement(Divider, {
    spacing: 12
  }), [['Horário', slot], ['Duração', '30 min'], ['Profissional', 'Dra. Helena Vasques'], ['Local', 'Sala 4 · 2º andar'], ['Código', '#AP-20418']].map(([k, v], i, arr) => /*#__PURE__*/React.createElement(React.Fragment, {
    key: k
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      padding: '8px 0'
    }
  }, /*#__PURE__*/React.createElement(MetaText, null, k), /*#__PURE__*/React.createElement("span", {
    className: k === 'Profissional' || k === 'Local' ? 'type-body-sm' : 'type-data-md'
  }, v)), i < arr.length - 1 ? /*#__PURE__*/React.createElement(Divider, null) : null))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 'var(--space-md)'
    }
  }, /*#__PURE__*/React.createElement(Button, {
    variant: "secondary",
    icon: "calendar-clock"
  }, "Reagendar"), /*#__PURE__*/React.createElement(Button, {
    variant: "danger",
    icon: "x"
  }, "Cancelar consulta")), /*#__PURE__*/React.createElement(MetaText, null, "Precisa de outro hor\xE1rio? ", /*#__PURE__*/React.createElement("a", {
    href: "#",
    onClick: e => {
      e.preventDefault();
      onRestart();
    }
  }, "Voltar ao in\xEDcio")));
}
Object.assign(window, {
  BookingConfirmed
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/patient-portal/BookingConfirmed.jsx", error: String((e && e.message) || e) }); }

// ui_kits/patient-portal/BookingForm.jsx
try { (() => {
const {
  Input,
  Button,
  Card,
  MetaText,
  Divider,
  Icon
} = window.ConsultRioDesignSystem_8bb054;
function BookingForm({
  slot,
  onBack,
  onSubmit
}) {
  const [name, setName] = React.useState('');
  const [phone, setPhone] = React.useState('');
  const [doc, setDoc] = React.useState('');
  const [touched, setTouched] = React.useState(false);
  const docError = touched && doc.replace(/\D/g, '').length !== 11 ? 'Informe um CPF válido, com 11 dígitos.' : '';
  const ready = name.trim() && phone.trim() && !docError && doc;
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-lg)'
    }
  }, /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("h1", {
    className: "type-headline-lg",
    style: {
      margin: 0
    }
  }, "Seus dados"), /*#__PURE__*/React.createElement("p", {
    className: "type-body-lg",
    style: {
      maxWidth: 'var(--measure-portal)',
      margin: '12px 0 0'
    }
  }, "Precisamos apenas do necess\xE1rio para reservar o hor\xE1rio no seu nome.")), /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)'
    }
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "calendar-check",
    size: 16,
    color: "var(--color-primary)"
  }), /*#__PURE__*/React.createElement("span", {
    className: "type-data-lg"
  }, slot), /*#__PURE__*/React.createElement(MetaText, null, "\xB7 Dra. Helena Vasques \xB7 30 min"))), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-lg)',
      maxWidth: 420
    }
  }, /*#__PURE__*/React.createElement(Input, {
    label: "Nome completo",
    value: name,
    onChange: setName,
    required: true,
    hint: "Como consta no documento."
  }), /*#__PURE__*/React.createElement(Input, {
    label: "Telefone com DDD",
    type: "tel",
    value: phone,
    onChange: setPhone,
    required: true,
    hint: "Enviamos a confirma\xE7\xE3o por SMS."
  }), /*#__PURE__*/React.createElement(Input, {
    label: "CPF",
    value: doc,
    onChange: v => {
      setDoc(v);
      setTouched(true);
    },
    required: true,
    error: docError
  })), /*#__PURE__*/React.createElement(Divider, null), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 'var(--space-md)'
    }
  }, /*#__PURE__*/React.createElement(Button, {
    variant: "secondary",
    icon: "chevron-left",
    onClick: onBack
  }, "Voltar"), /*#__PURE__*/React.createElement(Button, {
    variant: "primary",
    disabled: !ready,
    onClick: onSubmit
  }, "Confirmar reserva")));
}
Object.assign(window, {
  BookingForm
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/patient-portal/BookingForm.jsx", error: String((e && e.message) || e) }); }

// ui_kits/patient-portal/PortalChrome.jsx
try { (() => {
const {
  Icon,
  MetaText,
  Divider
} = window.ConsultRioDesignSystem_8bb054;
function PortalHeader() {
  return /*#__PURE__*/React.createElement("header", {
    style: {
      borderBottom: '1px solid var(--border)',
      background: 'var(--surface)'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      maxWidth: 960,
      margin: '0 auto',
      padding: '16px var(--margin)',
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-headline-md",
    style: {
      color: 'var(--color-primary)'
    }
  }, "Consult\xF3rio"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)',
      color: 'var(--text-meta)'
    }
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "phone",
    size: 16
  }), /*#__PURE__*/React.createElement(MetaText, {
    mono: true
  }, "(81) 3221-0400"))));
}
function PortalFooter() {
  return /*#__PURE__*/React.createElement("footer", {
    style: {
      maxWidth: 960,
      margin: '0 auto',
      padding: '0 var(--margin) var(--space-xl)'
    }
  }, /*#__PURE__*/React.createElement(Divider, {
    spacing: 20
  }), /*#__PURE__*/React.createElement(MetaText, null, "Cl\xEDnica S\xE3o Rafael \xB7 Rua da Aurora 320, Recife \xB7 Atendimento de segunda a s\xE1bado"));
}
function Steps({
  current
}) {
  const steps = ['Horário', 'Seus dados', 'Confirmação'];
  return /*#__PURE__*/React.createElement("ol", {
    style: {
      listStyle: 'none',
      display: 'flex',
      gap: 'var(--space-lg)',
      padding: 0,
      margin: 0
    }
  }, steps.map((s, i) => {
    const done = i < current,
      active = i === current;
    return /*#__PURE__*/React.createElement("li", {
      key: s,
      style: {
        display: 'flex',
        alignItems: 'center',
        gap: 'var(--space-sm)'
      }
    }, /*#__PURE__*/React.createElement("span", {
      style: {
        width: 20,
        height: 20,
        borderRadius: 'var(--radius-full)',
        display: 'inline-flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: active || done ? 'var(--color-primary)' : 'var(--surface-raised)',
        color: active || done ? 'var(--text-on-primary)' : 'var(--text-meta)',
        border: active || done ? 0 : '1px solid var(--border)'
      }
    }, done ? /*#__PURE__*/React.createElement(Icon, {
      name: "check",
      size: 12
    }) : /*#__PURE__*/React.createElement("span", {
      className: "type-data-sm"
    }, i + 1)), /*#__PURE__*/React.createElement("span", {
      className: "type-label-md",
      style: {
        color: active ? 'var(--text-body)' : 'var(--text-meta)'
      }
    }, s));
  }));
}
Object.assign(window, {
  PortalHeader,
  PortalFooter,
  Steps
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/patient-portal/PortalChrome.jsx", error: String((e && e.message) || e) }); }

// ui_kits/patient-portal/SlotPicker.jsx
try { (() => {
const {
  SlotChip,
  Card,
  MetaText,
  Icon,
  Button,
  Divider
} = window.ConsultRioDesignSystem_8bb054;
const DAYS = [{
  key: 'qui',
  label: 'Qui',
  date: '14 ago',
  free: ['09:00', '09:30', '10:30', '11:00', '14:00', '14:30', '16:00'],
  taken: ['10:00', '11:30', '15:00']
}, {
  key: 'sex',
  label: 'Sex',
  date: '15 ago',
  free: ['08:30', '11:00', '11:30', '15:30'],
  taken: ['09:00', '09:30', '10:00', '14:00', '16:30']
}, {
  key: 'sab',
  label: 'Sáb',
  date: '16 ago',
  free: ['09:00', '09:30', '10:00'],
  taken: ['10:30']
}];
function SlotPicker({
  selected,
  onSelect,
  onContinue
}) {
  const [day, setDay] = React.useState('qui');
  const current = DAYS.find(d => d.key === day);
  const all = [...current.free.map(t => ({
    t,
    state: 'free'
  })), ...current.taken.map(t => ({
    t,
    state: 'taken'
  }))].sort((a, b) => a.t.localeCompare(b.t));
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-lg)'
    }
  }, /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("h1", {
    className: "type-display",
    style: {
      margin: 0
    }
  }, "Quando voc\xEA pode ser atendido"), /*#__PURE__*/React.createElement("p", {
    className: "type-body-lg",
    style: {
      maxWidth: 'var(--measure-portal)',
      color: 'var(--text-body)',
      margin: '12px 0 0'
    }
  }, "Escolha um hor\xE1rio livre com a Dra. Helena Vasques. A confirma\xE7\xE3o chega por SMS no mesmo minuto.")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)'
    }
  }, /*#__PURE__*/React.createElement("button", {
    "aria-label": "Semana anterior",
    style: btnNav
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "chevron-left",
    size: 16
  })), DAYS.map(d => {
    const on = d.key === day;
    return /*#__PURE__*/React.createElement("button", {
      key: d.key,
      onClick: () => setDay(d.key),
      style: {
        minHeight: 'var(--touch-min)',
        padding: '8px 16px',
        borderRadius: 'var(--radius-sm)',
        cursor: 'pointer',
        background: on ? 'var(--color-primary)' : 'var(--surface)',
        color: on ? 'var(--text-on-primary)' : 'var(--text-body)',
        border: on ? 0 : '1px solid var(--border)',
        display: 'flex',
        flexDirection: 'column',
        alignItems: 'flex-start',
        gap: 2
      }
    }, /*#__PURE__*/React.createElement("span", {
      className: "type-label-caps"
    }, d.label), /*#__PURE__*/React.createElement("span", {
      className: "type-data-md"
    }, d.date));
  }), /*#__PURE__*/React.createElement("button", {
    "aria-label": "Semana seguinte",
    style: btnNav
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "chevron-right",
    size: 16
  })), /*#__PURE__*/React.createElement("span", {
    style: {
      marginLeft: 'auto',
      display: 'inline-flex',
      alignItems: 'center',
      gap: 6
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 12,
      height: 12,
      borderRadius: 'var(--radius-sm)',
      background: 'var(--surface-slot-free)'
    }
  }), /*#__PURE__*/React.createElement(MetaText, null, current.free.length, " hor\xE1rios livres"))), /*#__PURE__*/React.createElement(Card, null, /*#__PURE__*/React.createElement("div", {
    className: "type-label-caps",
    style: {
      color: 'var(--text-meta)'
    }
  }, "Manh\xE3 e tarde \xB7 ", current.label, " ", current.date), /*#__PURE__*/React.createElement("div", {
    style: {
      height: 12
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'grid',
      gridTemplateColumns: 'repeat(auto-fill, minmax(104px, 1fr))',
      gap: 'var(--space-sm)'
    }
  }, all.map(s => /*#__PURE__*/React.createElement(SlotChip, {
    key: s.t,
    time: s.t,
    state: s.state,
    selected: selected === s.t,
    onSelect: onSelect
  }))), /*#__PURE__*/React.createElement(Divider, {
    spacing: 16
  }), /*#__PURE__*/React.createElement(MetaText, null, "Hor\xE1rios marcados como Ocupado j\xE1 foram reservados por outro paciente.")), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-lg)'
    }
  }, /*#__PURE__*/React.createElement(Button, {
    variant: "primary",
    iconEnd: "arrow-right",
    disabled: !selected,
    onClick: onContinue
  }, "Continuar"), selected ? /*#__PURE__*/React.createElement(MetaText, {
    mono: true
  }, current.label, " ", current.date, " \xB7 ", selected, " \xB7 30 min") : /*#__PURE__*/React.createElement(MetaText, null, "Selecione um hor\xE1rio para continuar.")));
}
const btnNav = {
  width: 44,
  height: 44,
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  background: 'var(--surface)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--text-meta)',
  cursor: 'pointer'
};
Object.assign(window, {
  SlotPicker,
  PORTAL_DAYS: DAYS
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/patient-portal/SlotPicker.jsx", error: String((e && e.message) || e) }); }

// ui_kits/staff-console/ConflictRail.jsx
try { (() => {
const {
  ConflictBanner,
  Card,
  MetaText,
  Divider,
  StatusTag,
  Icon
} = window.ConsultRioDesignSystem_8bb054;
function ConflictRail({
  conflict,
  onReschedule,
  onCancel,
  syncedAt
}) {
  return /*#__PURE__*/React.createElement("aside", {
    style: {
      width: 320,
      borderLeft: '1px solid var(--border)',
      padding: 'var(--space-lg)',
      display: 'flex',
      flexDirection: 'column',
      gap: 'var(--space-lg)',
      overflow: 'auto'
    }
  }, /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("div", {
    className: "type-label-caps",
    style: {
      color: 'var(--text-meta)'
    }
  }, "Reconcilia\xE7\xE3o"), /*#__PURE__*/React.createElement("div", {
    style: {
      height: 8
    }
  }), conflict ? /*#__PURE__*/React.createElement(ConflictBanner, {
    detail: "A agenda externa da Dra. Helena bloqueia um hor\xE1rio j\xE1 reservado.",
    appointment: "14:00 \xB7 Marina Alves \xB7 30 min",
    external: "13:30\u201315:00 \xB7 Google Calendar",
    onReschedule: onReschedule,
    onCancel: onCancel
  }) : /*#__PURE__*/React.createElement(Card, {
    padding: "md"
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)'
    }
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "check",
    size: 16,
    color: "var(--color-primary)"
  }), /*#__PURE__*/React.createElement("span", {
    className: "type-body-sm"
  }, "Nenhum conflito em aberto.")), /*#__PURE__*/React.createElement("div", {
    style: {
      height: 4
    }
  }), /*#__PURE__*/React.createElement(MetaText, {
    mono: true
  }, "\xFAltima verifica\xE7\xE3o ", syncedAt))), /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("div", {
    className: "type-label-caps",
    style: {
      color: 'var(--text-meta)'
    }
  }, "Fila da recep\xE7\xE3o"), /*#__PURE__*/React.createElement("div", {
    style: {
      height: 8
    }
  }), [['Célia Monteiro', '09:15', 'aguardando'], ['Íris Coelho', '14:45', 'confirmada'], ['Hugo Namora', '15:30', 'confirmada']].map(([n, t, s], i, a) => /*#__PURE__*/React.createElement(React.Fragment, {
    key: n
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      alignItems: 'center',
      padding: '8px 0'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-body-sm"
  }, n), /*#__PURE__*/React.createElement(MetaText, null, s)), /*#__PURE__*/React.createElement("span", {
    className: "type-data-md"
  }, t)), i < a.length - 1 ? /*#__PURE__*/React.createElement(Divider, null) : null))), /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("div", {
    className: "type-label-caps",
    style: {
      color: 'var(--text-meta)'
    }
  }, "Resumo do dia"), /*#__PURE__*/React.createElement("div", {
    style: {
      height: 8
    }
  }), /*#__PURE__*/React.createElement(Card, {
    padding: "md"
  }, [['Agendadas', '9'], ['Concluídas', '2'], ['Não veio', '1'], ['Duração média', '32 min']].map(([k, v], i, a) => /*#__PURE__*/React.createElement(React.Fragment, {
    key: k
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      padding: '6px 0'
    }
  }, /*#__PURE__*/React.createElement(MetaText, null, k), /*#__PURE__*/React.createElement("span", {
    className: "type-data-md"
  }, v)), i < a.length - 1 ? /*#__PURE__*/React.createElement(Divider, null) : null))), /*#__PURE__*/React.createElement("div", {
    style: {
      height: 12
    }
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 6,
      flexWrap: 'wrap'
    }
  }, /*#__PURE__*/React.createElement(StatusTag, {
    status: "scheduled"
  }), /*#__PURE__*/React.createElement(StatusTag, {
    status: "completed"
  }), /*#__PURE__*/React.createElement(StatusTag, {
    status: "noshow"
  }))));
}
Object.assign(window, {
  ConflictRail
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/staff-console/ConflictRail.jsx", error: String((e && e.message) || e) }); }

// ui_kits/staff-console/ConsoleShell.jsx
try { (() => {
const {
  Icon,
  MetaText,
  Divider,
  Tooltip,
  Button
} = window.ConsultRioDesignSystem_8bb054;
const NAV = [{
  icon: 'calendar-check',
  label: 'Agenda do dia'
}, {
  icon: 'user',
  label: 'Pacientes'
}, {
  icon: 'clock',
  label: 'Fila da recepção'
}, {
  icon: 'refresh-cw',
  label: 'Reconciliação',
  badge: 2
}, {
  icon: 'filter',
  label: 'Relatórios'
}];
function Sidebar({
  active,
  onSelect
}) {
  return /*#__PURE__*/React.createElement("nav", {
    style: {
      width: 232,
      borderRight: '1px solid var(--border)',
      background: 'var(--surface-raised)',
      display: 'flex',
      flexDirection: 'column'
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      padding: 'var(--space-md) var(--space-md)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-headline-md",
    style: {
      color: 'var(--color-primary)'
    }
  }, "Consult\xF3rio"), /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement(MetaText, null, "Cl\xEDnica S\xE3o Rafael"))), /*#__PURE__*/React.createElement(Divider, null), /*#__PURE__*/React.createElement("ul", {
    style: {
      listStyle: 'none',
      margin: 0,
      padding: 'var(--space-sm)',
      display: 'flex',
      flexDirection: 'column',
      gap: 2
    }
  }, NAV.map(n => {
    const on = n.label === active;
    return /*#__PURE__*/React.createElement("li", {
      key: n.label
    }, /*#__PURE__*/React.createElement("button", {
      onClick: () => onSelect(n.label),
      style: {
        width: '100%',
        display: 'flex',
        alignItems: 'center',
        gap: 'var(--space-sm)',
        padding: '8px 12px',
        cursor: 'pointer',
        background: on ? 'var(--color-primary)' : 'transparent',
        color: on ? 'var(--text-on-primary)' : 'var(--text-body)',
        border: 0,
        borderRadius: 'var(--radius-sm)',
        textAlign: 'left'
      }
    }, /*#__PURE__*/React.createElement(Icon, {
      name: n.icon,
      size: 16
    }), /*#__PURE__*/React.createElement("span", {
      className: "type-body-sm",
      style: {
        flex: 1
      }
    }, n.label), n.badge ? /*#__PURE__*/React.createElement("span", {
      className: "type-data-sm",
      style: {
        background: on ? 'var(--surface)' : 'var(--color-tertiary)',
        color: 'var(--text-body)',
        padding: '0 5px',
        borderRadius: 'var(--radius-sm)'
      }
    }, n.badge) : null));
  })), /*#__PURE__*/React.createElement("div", {
    style: {
      marginTop: 'auto',
      padding: 'var(--space-md)'
    }
  }, /*#__PURE__*/React.createElement(Divider, {
    spacing: 8
  }), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 28,
      height: 28,
      borderRadius: 'var(--radius-full)',
      background: 'var(--color-primary)',
      color: 'var(--text-on-primary)',
      display: 'inline-flex',
      alignItems: 'center',
      justifyContent: 'center'
    },
    className: "type-label-md"
  }, "AR"), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-label-md"
  }, "Ana Ribeiro"), /*#__PURE__*/React.createElement(MetaText, null, "Recep\xE7\xE3o")))));
}
function DayHeader({
  occupancy,
  onSync,
  syncedAt
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      borderBottom: '1px solid var(--border)',
      padding: 'var(--space-md) var(--space-lg)',
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-lg)'
    }
  }, /*#__PURE__*/React.createElement("div", null, /*#__PURE__*/React.createElement("h1", {
    className: "type-headline-lg",
    style: {
      margin: 0
    }
  }, "Quinta, 14 de agosto"), /*#__PURE__*/React.createElement(MetaText, {
    mono: true
  }, "14 consultas \xB7 ocupa\xE7\xE3o ", occupancy)), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)',
      marginLeft: 'auto'
    }
  }, /*#__PURE__*/React.createElement("button", {
    "aria-label": "Dia anterior",
    style: navBtn
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "chevron-left",
    size: 16
  })), /*#__PURE__*/React.createElement("button", {
    "aria-label": "Dia seguinte",
    style: navBtn
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "chevron-right",
    size: 16
  })), /*#__PURE__*/React.createElement(Tooltip, {
    content: `Sincronizado às ${syncedAt}`
  }, /*#__PURE__*/React.createElement(Button, {
    size: "sm",
    variant: "secondary",
    icon: "refresh-cw",
    onClick: onSync
  }, "Sincronizar")), /*#__PURE__*/React.createElement(Button, {
    size: "sm",
    variant: "primary",
    icon: "plus"
  }, "Novo agendamento")));
}
function StatusBar({
  syncedAt,
  conflicts
}) {
  return /*#__PURE__*/React.createElement("div", {
    style: {
      borderTop: '1px solid var(--border)',
      padding: '6px var(--space-lg)',
      display: 'flex',
      gap: 'var(--space-lg)',
      alignItems: 'center',
      background: 'var(--surface-raised)'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-data-sm",
    style: {
      color: 'var(--text-meta)'
    }
  }, "SINCRONIZADO ", syncedAt), /*#__PURE__*/React.createElement("span", {
    className: "type-data-sm",
    style: {
      color: 'var(--text-meta)'
    }
  }, "2 AGENDAS EXTERNAS"), /*#__PURE__*/React.createElement("span", {
    style: {
      display: 'inline-flex',
      alignItems: 'center',
      gap: 6,
      marginLeft: 'auto'
    }
  }, /*#__PURE__*/React.createElement("span", {
    style: {
      width: 8,
      height: 8,
      borderRadius: 'var(--radius-full)',
      background: conflicts ? 'var(--color-tertiary)' : 'var(--color-primary)'
    }
  }), /*#__PURE__*/React.createElement("span", {
    className: "type-data-sm",
    style: {
      color: 'var(--text-meta)'
    }
  }, conflicts ? `${conflicts} CONFLITO(S) EM ABERTO` : 'NENHUM CONFLITO')));
}
const navBtn = {
  width: 32,
  height: 32,
  display: 'inline-flex',
  alignItems: 'center',
  justifyContent: 'center',
  background: 'var(--surface)',
  border: '1px solid var(--border)',
  borderRadius: 'var(--radius-sm)',
  color: 'var(--text-meta)',
  cursor: 'pointer'
};
Object.assign(window, {
  Sidebar,
  DayHeader,
  StatusBar
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/staff-console/ConsoleShell.jsx", error: String((e && e.message) || e) }); }

// ui_kits/staff-console/DayTable.jsx
try { (() => {
const {
  DataTable,
  StatusTag,
  MetaText,
  Icon,
  Button,
  Divider,
  Card
} = window.ConsultRioDesignSystem_8bb054;
const ROWS = [{
  id: 1,
  time: '08:00',
  patient: 'Marina Alves',
  prof: 'Dra. Helena Vasques',
  duration: '30 min',
  room: 'Sala 4',
  status: 'completed',
  ref: '#AP-20401'
}, {
  id: 2,
  time: '08:30',
  patient: 'Rui Sampaio',
  prof: 'Dr. Nuno Braga',
  duration: '45 min',
  room: 'Sala 2',
  status: 'completed',
  ref: '#AP-20404'
}, {
  id: 3,
  time: '09:15',
  patient: 'Célia Monteiro',
  prof: 'Dra. Helena Vasques',
  duration: '30 min',
  room: 'Sala 4',
  status: 'scheduled',
  ref: '#AP-20409'
}, {
  id: 4,
  time: '10:00',
  patient: 'Tomás Ferraz',
  prof: 'Dr. Nuno Braga',
  duration: '20 min',
  room: 'Sala 2',
  status: 'noshow',
  ref: '#AP-20411'
}, {
  id: 5,
  time: '10:30',
  patient: 'Beatriz Lousada',
  prof: 'Dra. Helena Vasques',
  duration: '30 min',
  room: 'Sala 4',
  status: 'rescheduled',
  ref: '#AP-20413'
}, {
  id: 6,
  time: '11:15',
  patient: 'Sérgio Pina',
  prof: 'Dr. Nuno Braga',
  duration: '30 min',
  room: 'Sala 2',
  status: 'cancelled',
  ref: '#AP-20415'
}, {
  id: 7,
  time: '14:00',
  patient: 'Marina Alves',
  prof: 'Dra. Helena Vasques',
  duration: '30 min',
  room: 'Sala 4',
  status: 'conflict',
  ref: '#AP-20418'
}, {
  id: 8,
  time: '14:45',
  patient: 'Íris Coelho',
  prof: 'Dra. Helena Vasques',
  duration: '30 min',
  room: 'Sala 4',
  status: 'scheduled',
  ref: '#AP-20420'
}, {
  id: 9,
  time: '15:30',
  patient: 'Hugo Namora',
  prof: 'Dr. Nuno Braga',
  duration: '45 min',
  room: 'Sala 2',
  status: 'scheduled',
  ref: '#AP-20422'
}];
const FILTERS = ['Todos', 'Agendados', 'Conflitos', 'Concluídos'];
function DayTable({
  rows,
  onRowClick,
  selectedId
}) {
  const [filter, setFilter] = React.useState('Todos');
  const visible = rows.filter(r => filter === 'Todos' ? true : filter === 'Agendados' ? r.status === 'scheduled' : filter === 'Conflitos' ? r.status === 'conflict' : r.status === 'completed');
  return /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      flexDirection: 'column',
      minHeight: 0
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      gap: 'var(--space-sm)',
      padding: 'var(--space-sm) var(--space-lg)'
    }
  }, FILTERS.map(f => /*#__PURE__*/React.createElement("button", {
    key: f,
    onClick: () => setFilter(f),
    className: "type-label-caps",
    style: {
      padding: '6px 10px',
      cursor: 'pointer',
      borderRadius: 'var(--radius-sm)',
      background: filter === f ? 'var(--surface-raised)' : 'transparent',
      color: filter === f ? 'var(--color-primary)' : 'var(--text-meta)',
      border: filter === f ? '1px solid var(--border)' : '1px solid transparent'
    }
  }, f)), /*#__PURE__*/React.createElement("span", {
    style: {
      marginLeft: 'auto',
      display: 'inline-flex',
      alignItems: 'center',
      gap: 6,
      color: 'var(--text-meta)'
    }
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "search",
    size: 14
  }), /*#__PURE__*/React.createElement(MetaText, {
    mono: true
  }, visible.length, "/", rows.length))), /*#__PURE__*/React.createElement("div", {
    style: {
      overflow: 'auto',
      padding: '0 var(--space-lg) var(--space-lg)'
    }
  }, /*#__PURE__*/React.createElement(DataTable, {
    onRowClick: onRowClick,
    columns: [{
      key: 'time',
      header: 'Horário',
      mono: true,
      width: 82
    }, {
      key: 'patient',
      header: 'Paciente',
      render: r => /*#__PURE__*/React.createElement("span", {
        style: {
          display: 'inline-flex',
          alignItems: 'center',
          gap: 6,
          fontWeight: r.id === selectedId ? 600 : 400
        }
      }, r.status === 'conflict' ? /*#__PURE__*/React.createElement(Icon, {
        name: "alert-triangle",
        size: 14,
        color: "var(--color-tertiary)"
      }) : null, r.patient)
    }, {
      key: 'prof',
      header: 'Profissional',
      meta: true
    }, {
      key: 'room',
      header: 'Sala',
      meta: true,
      width: 76
    }, {
      key: 'ref',
      header: 'Código',
      mono: true,
      meta: true,
      width: 96
    }, {
      key: 'duration',
      header: 'Duração',
      mono: true,
      align: 'right',
      width: 84
    }, {
      key: 'status',
      header: 'Status',
      width: 148,
      render: r => /*#__PURE__*/React.createElement(StatusTag, {
        status: r.status
      })
    }],
    rows: visible
  })));
}
function DetailDrawer({
  row,
  onClose
}) {
  if (!row) return null;
  return /*#__PURE__*/React.createElement(Card, {
    padding: "md",
    style: {
      background: 'var(--surface)',
      boxShadow: 'var(--shadow-float)',
      position: 'absolute',
      right: 20,
      bottom: 44,
      width: 320,
      zIndex: 10
    }
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      alignItems: 'center',
      justifyContent: 'space-between'
    }
  }, /*#__PURE__*/React.createElement("span", {
    className: "type-headline-md"
  }, row.patient), /*#__PURE__*/React.createElement("button", {
    onClick: onClose,
    "aria-label": "Fechar",
    style: {
      background: 'transparent',
      border: 0,
      cursor: 'pointer',
      color: 'var(--text-meta)'
    }
  }, /*#__PURE__*/React.createElement(Icon, {
    name: "x",
    size: 16
  }))), /*#__PURE__*/React.createElement(Divider, {
    spacing: 10
  }), [['Horário', row.time], ['Duração', row.duration], ['Profissional', row.prof], ['Sala', row.room], ['Código', row.ref]].map(([k, v], i, a) => /*#__PURE__*/React.createElement(React.Fragment, {
    key: k
  }, /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      justifyContent: 'space-between',
      padding: '6px 0'
    }
  }, /*#__PURE__*/React.createElement(MetaText, null, k), /*#__PURE__*/React.createElement("span", {
    className: k === 'Profissional' || k === 'Sala' ? 'type-body-sm' : 'type-data-md'
  }, v)), i < a.length - 1 ? /*#__PURE__*/React.createElement(Divider, null) : null)), /*#__PURE__*/React.createElement("div", {
    style: {
      display: 'flex',
      gap: 'var(--space-sm)',
      marginTop: 'var(--space-md)'
    }
  }, /*#__PURE__*/React.createElement(Button, {
    size: "sm",
    variant: "secondary",
    icon: "calendar-clock"
  }, "Reagendar"), /*#__PURE__*/React.createElement(Button, {
    size: "sm",
    variant: "danger",
    icon: "x"
  }, "Cancelar")));
}
Object.assign(window, {
  DayTable,
  DetailDrawer,
  CONSOLE_ROWS: ROWS
});
})(); } catch (e) { __ds_ns.__errors.push({ path: "ui_kits/staff-console/DayTable.jsx", error: String((e && e.message) || e) }); }

__ds_ns.Button = __ds_scope.Button;

__ds_ns.Card = __ds_scope.Card;

__ds_ns.Divider = __ds_scope.Divider;

__ds_ns.Icon = __ds_scope.Icon;

__ds_ns.MetaText = __ds_scope.MetaText;

__ds_ns.DataTable = __ds_scope.DataTable;

__ds_ns.Tooltip = __ds_scope.Tooltip;

__ds_ns.Input = __ds_scope.Input;

__ds_ns.ConflictBanner = __ds_scope.ConflictBanner;

__ds_ns.SlotChip = __ds_scope.SlotChip;

__ds_ns.StatusTag = __ds_scope.StatusTag;

})();
