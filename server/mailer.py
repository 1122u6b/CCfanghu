"""Sends website emails (registration verification codes) through the owner's own mailbox.

The owner fills in the sending mailbox in the management center (安全 page): for a QQ mailbox that is
smtp.qq.com, port 465, the mailbox address and its SMTP authorization code. The authorization code is
stored only on the server (file mode 600) and is never sent back to the browser.
"""
import json, os, re, smtplib, ssl
from email.header import Header
from email.mime.text import MIMEText
from email.utils import formataddr
from pathlib import Path

EMAIL_RE = re.compile(r'^[A-Za-z0-9._%+\-]{1,64}@[A-Za-z0-9.\-]{1,190}\.[A-Za-z]{2,24}$')


class MailError(ValueError):
    pass


class Mailer:
    def __init__(self, root):
        self.path = Path(root) / 'mail-settings.json'

    def settings(self):
        try:
            return json.loads(self.path.read_text())
        except (OSError, ValueError):
            return {}

    def configured(self):
        s = self.settings()
        return bool(s.get('host') and s.get('user') and s.get('password'))

    def info(self):
        s = self.settings()
        return {'configured': self.configured(), 'host': s.get('host', 'smtp.qq.com'), 'port': s.get('port', 465),
                'user': s.get('user', ''), 'sender_name': s.get('sender_name', 'Leyra')}

    def save(self, host, port, user, password, sender_name='Leyra'):
        host = str(host or '').strip(); user = str(user or '').strip(); name = str(sender_name or 'Leyra').strip()[:40] or 'Leyra'
        if not re.fullmatch(r'[A-Za-z0-9.\-]{3,190}', host):
            raise MailError('发件服务器地址不对，QQ 邮箱填 smtp.qq.com')
        try:
            port = int(port)
        except (TypeError, ValueError):
            raise MailError('端口要填数字，QQ 邮箱填 465') from None
        if port not in (465, 587):
            raise MailError('端口只支持 465（SSL）或 587（STARTTLS）')
        if not EMAIL_RE.match(user):
            raise MailError('发件邮箱格式不对')
        old = self.settings()
        password = str(password or '') or old.get('password', '')
        if not password or len(password) > 200:
            raise MailError('请填写邮箱的 SMTP 授权码')
        data = {'host': host, 'port': port, 'user': user, 'password': password, 'sender_name': name}
        tmp = self.path.with_suffix('.tmp')
        tmp.write_text(json.dumps(data)); os.chmod(tmp, 0o600); tmp.replace(self.path)
        return self.info()

    def send(self, to, subject, body):
        s = self.settings()
        if not self.configured():
            raise MailError('还没有设置发件邮箱')
        msg = MIMEText(body, 'plain', 'utf-8')
        msg['Subject'] = Header(subject, 'utf-8')
        msg['From'] = formataddr((str(Header(s.get('sender_name') or 'Leyra', 'utf-8')), s['user']))
        msg['To'] = to
        context = ssl.create_default_context()
        try:
            if int(s['port']) == 465:
                with smtplib.SMTP_SSL(s['host'], 465, timeout=20, context=context) as smtp:
                    smtp.login(s['user'], s['password']); smtp.sendmail(s['user'], [to], msg.as_string())
            else:
                with smtplib.SMTP(s['host'], int(s['port']), timeout=20) as smtp:
                    smtp.starttls(context=context); smtp.login(s['user'], s['password']); smtp.sendmail(s['user'], [to], msg.as_string())
        except smtplib.SMTPAuthenticationError:
            raise MailError('发件邮箱登录失败：请检查邮箱地址和 SMTP 授权码（不是邮箱登录密码）') from None
        except smtplib.SMTPRecipientsRefused:
            raise MailError('对方邮箱地址不存在或拒收') from None
        except (smtplib.SMTPException, OSError) as e:
            raise MailError('邮件发送失败，请稍后再试（' + type(e).__name__ + '）') from None
